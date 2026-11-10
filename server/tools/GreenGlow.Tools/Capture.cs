// Dev only: captures TN5250 sessions on a test IBM i and records them. It connects to the
// IBM i directly, and sends only protocol replies (Telnet negotiation, the QUERY reply,
// Cancel Invite and negative responses to records it can't process), never keys or screen
// contents.
//
//   capture <target>              one session, stopping at the first screen
//   capture <target> --collision  a second session asks for the same device
//   capture <target> --reconnect  close, then reconnect at once with the same device
//
// The target's address is --url, or the enabled connection of that name in the server's
// database (which must be marked as a test system). Needs CAPTURE_DEVICE in .env, and the
// target listed in CAPTURE_TARGETS (default: test). Recordings go to test/recordings/
// (gitignored) because they hold system and device names.

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GreenGlow.Engine;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Tools;

public static class Capture
{
    private const string TerminalType = "IBM-3477-FC";
    private static readonly TimeSpan ScreenTimeout = TimeSpan.FromSeconds(20);

    public sealed class Failure(string message) : Exception(message);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await CaptureAsync(args);
        }
        catch (Failure e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(string[] args)
    {
        string? name = null, mode = null, url = null, db = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--collision" or "--reconnect" when mode is null:
                    mode = args[i];
                    break;
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--db" when i + 1 < args.Length:
                    db = args[++i];
                    break;
                case var arg when !arg.StartsWith("--") && name is null:
                    name = arg;
                    break;
                default:
                    throw new Failure($"Unknown option {args[i]}");
            }
        }
        if (name is null)
            throw new Failure("Usage: npm run capture -- <target> [--collision | --reconnect] [--url tcp://host:port] [--db <path>]");

        var allowed = (Env.Get("CAPTURE_TARGETS") ?? "test").Split(',', StringSplitOptions.TrimEntries);
        if (!allowed.Contains(name))
            throw new Failure($"Target \"{name}\" is not in CAPTURE_TARGETS ({string.Join(", ", allowed)}). Capture test systems only.");
        var device = Env.Get("CAPTURE_DEVICE") ?? "";
        if (!TelnetNegotiator.IsValidDeviceName(device))
            throw new Failure("Set CAPTURE_DEVICE in .env to a device name no GreenGlow server hands out, such as GG9999.");

        var target = url is not null ? ParseUrl(name, url) : await FromDatabaseAsync(name, db);
        var connector = HostConnector.Create(Env.FromRoot(Env.Get("Relay__CaFile") ?? "certs/ibmi-ca.pem"));
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'");

        Session Open(string label) => new(label, target, device, connector, stamp);

        var first = Open("a");
        var reachedScreen = await first.ScreenOrCloseAsync();
        if (mode == "--collision" && reachedScreen)
        {
            Console.WriteLine("--- second session with the same device while the first is open");
            var second = Open("b");
            if (await second.ScreenOrCloseAsync()) await second.CloseAsync("capture done");
            await first.CloseAsync("capture done");
        }
        else if (mode == "--reconnect" && reachedScreen)
        {
            await first.CloseAsync("capture done");
            Console.WriteLine("--- reconnecting at once with the same device");
            var second = Open("b");
            if (await second.ScreenOrCloseAsync()) await second.CloseAsync("capture done");
        }
        else
        {
            if (reachedScreen) await Task.Delay(500);
            await first.CloseAsync("capture done");
        }
        return 0;
    }

    private static Target ParseUrl(string name, string url)
    {
        try
        {
            return TargetParser.ParseUrl(name, name, url);
        }
        catch (ConfigException e)
        {
            throw new Failure(e.Message);
        }
    }

    /// <summary>The enabled connection called `name` in the server's database, read-only.</summary>
    private static async Task<Target> FromDatabaseAsync(string name, string? db)
    {
        var path = db is not null
            ? Path.GetFullPath(db)
            : Env.Get("Data__DbPath") is { Length: > 0 } configured
                ? Env.FromRoot(configured)
                : new[] { "data/greenglow.db", "server/src/GreenGlow.Server/data/greenglow.db" }
                    .Select(Env.FromRoot).FirstOrDefault(File.Exists);
        if (path is null || !File.Exists(path))
            throw new Failure($"No server database{(path is null ? "" : $" at {path}")}: run the server once (npm run dev), or pass --url tcp://host:port or --db <path>.");
        HostConnection? row;
        try
        {
            var options = new DbContextOptionsBuilder<GreenGlowDb>().UseSqlite($"Data Source={path};Mode=ReadOnly").Options;
            await using var context = new GreenGlowDb(options);
            row = await context.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name && c.Enabled);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            throw new Failure($"Could not read connections from {path}: {e.Message}");
        }
        if (row is null) throw new Failure($"No enabled connection \"{name}\" in {path}; pass --url tcp://host:port.");
        if (!row.IsTest) throw new Failure($"Connection \"{name}\" is not marked as a test system. Capture test systems only.");
        Console.WriteLine($"target {name} from {path}");
        return TargetCatalog.ToTarget(row);
    }

    /// <summary>One Telnet session to the target, recorded with nondisplay input blanked.</summary>
    private sealed class Session
    {
        private readonly string _label;
        private readonly Target _target;
        private readonly string _stamp;
        private readonly Connection _connection;
        private readonly TelnetDecoder _hostTrace = new();
        private readonly TelnetDecoder _clientTrace = new();
        private readonly TaskCompletionSource _screen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<string> _done;
        private string? _closeReason;

        public Session(string label, Target target, string device, HostConnector connector, string stamp)
        {
            _label = label;
            _target = target;
            _stamp = stamp;
            var clock = Stopwatch.StartNew();
            // The connection records the session itself, scrubbed; capture never types anyway.
            _connection = new Connection(new ConnectionConfig
            {
                TerminalType = TerminalType,
                DeviceName = device,
                Recording = new RecordingOptions(() => clock.Elapsed.TotalMilliseconds, Blank.Nondisplay),
            });
            _done = RunAsync(connector, device);
        }

        private void Log(string text) => Console.WriteLine($"[{_label}] {text}");

        /// <summary>True at the first screen, false if the session closed first.</summary>
        public async Task<bool> ScreenOrCloseAsync() => await Task.WhenAny(_screen.Task, _done) == _screen.Task;

        public async Task CloseAsync(string reason)
        {
            _closeReason ??= reason;
            await _stop.CancelAsync();
            await _done;
        }

        private async Task<string> RunAsync(HostConnector connector, string device)
        {
            string reason;
            try
            {
                using var timeout = new CancellationTokenSource(ScreenTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, timeout.Token);
                var (client, stream) = await connector.ConnectAsync(_target, linked.Token);
                using (client)
                await using (stream)
                {
                    Log($"connected to {_target.Name} as {device} ({TerminalType}){(_target.Secure ? "" : ", plain Telnet")}");
                    reason = await PumpAsync(stream, linked.Token, timeout);
                }
            }
            catch (OperationCanceledException)
            {
                reason = _closeReason ?? $"no screen after {ScreenTimeout.TotalSeconds:0} s";
            }
            catch (ObjectDisposedException)
            {
                reason = _closeReason ?? "capture timeout";
            }
            catch (Exception e) when (e is IOException or SocketException or System.Security.Authentication.AuthenticationException)
            {
                reason = $"error: {e.Message}";
            }
            foreach (var e in _connection.Closed()) Report(e);
            var dir = Path.Combine(Env.RepoRoot, "test", "recordings");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{_stamp}-{_target.Name}-{_label}.bin");
            await File.WriteAllBytesAsync(file, _connection.Recording?.ToBytes() ?? []);
            Log($"closed ({reason}); recording {Path.GetRelativePath(Env.RepoRoot, file)}");
            return reason;
        }

        private async Task<string> PumpAsync(Stream stream, CancellationToken cancel, CancellationTokenSource timeout)
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer, cancel);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_stop.IsCancellationRequested)
                {
                    Log($"no screen after {ScreenTimeout.TotalSeconds:0} s; closing");
                    return "capture timeout";
                }
                if (n == 0) return "host closed";
                var data = buffer.AsSpan(0, n);
                foreach (var e in _hostTrace.Push(data)) Trace("host  ", e);
                var result = _connection.Receive(data);
                foreach (var send in result.Send)
                {
                    if (send.Kind is OutboundKind.Input or OutboundKind.HostRead)
                    {
                        await stream.DisposeAsync();
                        Console.Error.WriteLine(
                            $"Refusing to send {JsonSerializer.Serialize(send.Kind, Replay.Json).Trim('"')}: capture only answers protocol requests.");
                        Environment.Exit(1);
                    }
                    foreach (var e in _clientTrace.Push(send.Bytes)) Trace("client", e);
                    await stream.WriteAsync(send.Bytes, cancel);
                }
                foreach (var e in result.Events)
                {
                    Report(e);
                    if (e is ConnectionEvent.Record record && !Query.IsQuery(record.HostRecord.Data)) _screen.TrySetResult();
                }
            }
        }

        private void Trace(string side, TelnetEvent e)
        {
            switch (e)
            {
                case TelnetEvent.Negotiate n:
                    Log($"{side} {n.Command.ToString().ToUpperInvariant()} {Telnet.OptionName(n.Option)}");
                    break;
                case TelnetEvent.Subnegotiation s:
                    Log($"{side} SB {Telnet.OptionName(s.Option)} {Hex(s.Data)} | {Printable(s.Data)}");
                    break;
                case TelnetEvent.Control c:
                    Log($"{side} IAC {c.Code}");
                    break;
            }
        }

        private void Report(ConnectionEvent e)
        {
            switch (e)
            {
                case ConnectionEvent.Record { HostRecord: var record }:
                    Log($"host   5250 record: opcode 0x{record.Opcode:x2}, flags 0x{record.Flags:x2}, {record.Data.Length} data bytes");
                    Dump(record.Data);
                    break;
                case ConnectionEvent.Startup { Result: var r }:
                    Log($"startup response {r.Code} ({r.Message}); device {r.DeviceName}");
                    break;
                case ConnectionEvent.Screen:
                    // The record before it was dumped; the screen itself is in the recording.
                    break;
                default:
                    Log(JsonSerializer.Serialize(e, e.GetType(), Replay.Json));
                    break;
            }
        }
    }

    private static void Dump(byte[] data)
    {
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var row = data.AsSpan(offset, Math.Min(16, data.Length - offset)).ToArray();
            var text = new string(row.Select(b => b is >= 0x40 and < 0xff ? Ebcdic.ToChar(b) : '.').ToArray());
            Console.WriteLine($"    {offset:x4}  {Hex(row),-47}  {text}");
        }
    }

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("x2")));

    private static string Printable(byte[] bytes) =>
        new(bytes.Select(b => b is >= 0x20 and < 0x7f ? (char)b : '.').ToArray());
}
