using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GreenGlow.Engine;
using GreenGlow.FakeHost;
using GreenGlow.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

public static class Wait
{
    /// <summary>Polls until the condition holds; fails with a timeout after <see cref="T.Timeout"/>.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + T.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + T.Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

/// <summary>A screen message from the server, decoded for assertions.</summary>
public sealed class ScreenView(JsonElement snapshot)
{
    public JsonElement Snapshot { get; } = snapshot;

    public bool Unlocked => Snapshot.GetProperty("keyboard").GetString() == "unlocked";

    /// <summary>One line per row, nulls as blanks.</summary>
    public string Text
    {
        get
        {
            var columns = Snapshot.GetProperty("columns").GetInt32();
            var rows = Snapshot.GetProperty("rows").GetInt32();
            var text = Ebcdic.Decode(Snapshot.GetProperty("buffer").GetBytesFromBase64()).Replace('\0', ' ');
            return string.Join("\n", Enumerable.Range(0, rows).Select(r => text.Substring(r * columns, columns).TrimEnd()));
        }
    }

    /// <summary>The fields that take input (field format word bit 0x4000).</summary>
    public int InputFields => Snapshot.GetProperty("fields").EnumerateArray().Count(f => (f.GetProperty("ffw").GetInt32() & 0x4000) != 0);
}

/// <summary>A browser: a WebSocket on a session that collects the server's JSON messages.</summary>
public sealed class BrowserClient : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly List<JsonElement> _messages = [];
    private readonly Task _receiving;

    public BrowserClient(WebSocket ws)
    {
        _ws = ws;
        _receiving = Task.Run(ReceiveAsync);
    }

    public static async Task<BrowserClient> AttachAsync(RelayFactory factory, Guid sessionId, string upn = RelayFactory.DefaultUpn,
        bool takeOver = false) =>
        new(await factory.ConnectAsync(sessionId, upn: upn, takeOver: takeOver));

    public WebSocket Socket => _ws;

    public WebSocketCloseStatus? CloseStatus { get; private set; }
    public string? CloseReason { get; private set; }
    public bool Ended => _receiving.IsCompleted;

    public List<JsonElement> Messages
    {
        get
        {
            lock (_messages) return [.. _messages];
        }
    }

    /// <summary>How many messages have arrived; pass it as `since` to wait only for later ones.</summary>
    public int Mark
    {
        get
        {
            lock (_messages) return _messages.Count;
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
                    length += r.Count;
                } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = _ws.CloseStatus;
                    CloseReason = _ws.CloseStatusDescription;
                    // Answer it, as a browser does; the server waits for that before it lets go.
                    if (AnswerClose)
                    {
                        try
                        {
                            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        }
                        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
                        {
                            // The server had already let go.
                        }
                    }
                    return;
                }
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                lock (_messages) _messages.Add(doc.RootElement.Clone());
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The server dropped the socket without a close handshake.
        }
    }

    /// <summary>Answer the server's close frame, as a browser does. Off for a browser that hangs.</summary>
    public bool AnswerClose { get; set; } = true;

    public static string TypeOf(JsonElement message) => message.GetProperty("type").GetString() ?? "";

    /// <summary>The first message at or after `since` that matches.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, string what, int since = 0)
    {
        var deadline = DateTime.UtcNow + T.Timeout;
        var from = since;
        while (true)
        {
            var all = Messages;
            for (; from < all.Count; from++)
                if (match(all[from])) return all[from];
            if (_receiving.IsCompleted || DateTime.UtcNow > deadline)
                throw new TimeoutException($"waiting for {what}; got: {string.Join(", ", all.Select(TypeOf))}"
                    + (_receiving.IsCompleted ? $"; socket closed ({CloseStatus} {CloseReason})" : ""));
            await Task.Delay(10);
        }
    }

    public Task<JsonElement> WaitForTypeAsync(string type, int since = 0) =>
        WaitForAsync(m => TypeOf(m) == type, $"a {type} message", since);

    /// <summary>The first screen at or after `since` that matches.</summary>
    public async Task<ScreenView> WaitForScreenAsync(Func<ScreenView, bool> match, string what, int since = 0)
    {
        var message = await WaitForAsync(m => TypeOf(m) == "screen" && match(new ScreenView(m.GetProperty("snapshot"))),
            $"a screen {what}", since);
        return new ScreenView(message.GetProperty("snapshot"));
    }

    /// <summary>A screen with this text and the keyboard unlocked.</summary>
    public Task<ScreenView> WaitForTextAsync(string text, int since = 0) =>
        WaitForScreenAsync(s => s.Unlocked && s.Text.Contains(text), $"with '{text}'", since);

    /// <summary>Waits for the server to close the socket.</summary>
    public async Task<(WebSocketCloseStatus? Status, string? Reason)> WaitClosedAsync()
    {
        await _receiving.WaitAsync(T.Timeout);
        return (CloseStatus, CloseReason);
    }

    public Task SendRawAsync(string text, WebSocketMessageType type = WebSocketMessageType.Text) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(text), type, true, T.Token());

    public Task SendKeyAsync(object key) => SendRawAsync(JsonSerializer.Serialize(new { type = "key", key }));

    public Task PressAsync(string name) => SendKeyAsync(new { type = "function", name });

    public Task ClickAsync(int row, int column) => SendKeyAsync(new { type = "cursor", row, column });

    public async Task TypeAsync(string text)
    {
        foreach (var c in text) await SendKeyAsync(new { type = "character", @char = c.ToString() });
    }

    public Task TakeControlAsync() => SendRawAsync("""{"type":"take-control"}""");

    public Task ReturnControlAsync() => SendRawAsync("""{"type":"return-control"}""");

    /// <summary>Fills in the fake host's sign-on screen and presses Enter.</summary>
    public async Task SignOnAsync(string user, string password)
    {
        await ClickAsync(6, 53);
        await TypeAsync(user);
        await ClickAsync(7, 53);
        await TypeAsync(password);
        await PressAsync("Enter");
    }

    /// <summary>Closes the socket; the page says "user disconnected" when the user disconnects.</summary>
    public async Task CloseAsync(string reason = "bye")
    {
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, T.Token());
        await _receiving.WaitAsync(T.Timeout);
    }

    public async ValueTask DisposeAsync()
    {
        _ws.Abort();
        try
        {
            await _receiving.WaitAsync(T.Timeout);
        }
        finally
        {
            _ws.Dispose();
        }
    }
}

/// <summary>
/// The server under test with a fake IBM i behind a target called "test". Dispose stops the
/// server first (which ends its sessions), then the host.
/// </summary>
public sealed class Rig : IAsyncDisposable
{
    public FakeIbmI Host { get; }
    public RelayFactory Factory { get; }

    private Rig(FakeIbmI host, RelayFactory factory)
    {
        Host = host;
        Factory = factory;
    }

    /// <summary>`targets` is the Relay:Targets text; by default "test" is the fake host.</summary>
    public static async Task<Rig> StartAsync(int? max = null, Func<int, string>? targets = null, string? caFile = null)
    {
        var host = await FakeIbmI.StartAsync();
        return new Rig(host, RelayFactory.For(targets?.Invoke(host.Port) ?? $"test=tcp://127.0.0.1:{host.Port}", max: max, caFile: caFile));
    }

    /// <summary>A rig over a host and factory the caller owns (it disposes neither).</summary>
    public static Rig Wrap(FakeIbmI host, RelayFactory factory) => new(host, factory) { _borrowed = true };

    private bool _borrowed;

    public readonly record struct Opened(HttpStatusCode Status, JsonElement Body)
    {
        public Guid Id => Body.GetProperty("id").GetGuid();
        public string? Device => Body.GetProperty("device").GetString();
        public string Error => Body.GetProperty("error").GetString() ?? "";
    }

    public async Task<Opened> OpenAsync(string target = "test", string upn = RelayFactory.DefaultUpn, bool spare = false)
    {
        var client = await Factory.SignedInClientAsync(upn);
        var response = await client.PostAsync("/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { target, spare }), Encoding.UTF8, "application/json"), T.Token());
        var text = await response.Content.ReadAsStringAsync();
        return new Opened(response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    /// <summary>Opens a session that must succeed.</summary>
    public async Task<Opened> OpenOkAsync(string target = "test", string upn = RelayFactory.DefaultUpn, bool spare = false)
    {
        var opened = await OpenAsync(target, upn, spare);
        Assert.Equal(HttpStatusCode.Created, opened.Status);
        return opened;
    }

    /// <summary>Opens a session and attaches a browser, which has seen its hello by the time this returns.</summary>
    public async Task<(Opened Session, BrowserClient Browser)> OpenAndAttachAsync(string target = "test", string upn = RelayFactory.DefaultUpn)
    {
        var session = await OpenOkAsync(target, upn);
        var browser = await BrowserClient.AttachAsync(Factory, session.Id, upn);
        await browser.WaitForTypeAsync("hello");
        return (session, browser);
    }

    public async Task<(string Raw, List<JsonElement> Sessions)> ListAsync(string upn = RelayFactory.DefaultUpn)
    {
        var client = await Factory.SignedInClientAsync(upn);
        var raw = await client.GetStringAsync("/api/sessions", T.Token());
        return (raw, JsonDocument.Parse(raw).RootElement.GetProperty("sessions").EnumerateArray().Select(e => e.Clone()).ToList());
    }

    public async Task<HttpStatusCode> DeleteAsync(Guid id, string upn = RelayFactory.DefaultUpn)
    {
        var client = await Factory.SignedInClientAsync(upn);
        return (await client.DeleteAsync($"/api/sessions/{id}", T.Token())).StatusCode;
    }

    public Task WaitNoSessionsAsync(string upn = RelayFactory.DefaultUpn) =>
        Wait.UntilAsync(async () => (await ListAsync(upn)).Sessions.Count == 0, "the session list to be empty");

    public GreenGlowDb Db() => Factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();

    /// <summary>The log rows in the order they were written.</summary>
    public async Task<List<ConnectionLog>> LogRowsAsync(LogKind? kind = null)
    {
        await using var db = Db();
        var written = (await db.LogChanges.ToListAsync()).GroupBy(c => c.LogId).ToDictionary(g => g.Key, g => g.Min(c => c.Seq));
        return (await db.Logs.ToListAsync()).Where(l => kind is null || l.Kind == kind)
            .OrderBy(l => written.GetValueOrDefault(l.Id, long.MaxValue)).ToList();
    }

    /// <summary>Every string in every column of every table, as the database file holds them.</summary>
    public List<string> AllDatabaseStrings()
    {
        var path = Path.Combine(Factory.DbDirectory, "greenglow.db");
        var found = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table}\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.IsDBNull(i)) continue;
                        var value = reader.GetValue(i);
                        found.Add(value is byte[] bytes ? Encoding.Latin1.GetString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                    }
                }
            }
        }
        return found;
    }

    public async ValueTask DisposeAsync()
    {
        if (_borrowed) return;
        Factory.Dispose();
        await Host.DisposeAsync();
    }
}

/// <summary>
/// Sits between the server and a fake IBM i (or in place of one). It can wait on a gate before
/// doing anything with a connection, so a test can attach its browser before the host side
/// acts. It can terminate TLS, and can instead reset the connection.
/// </summary>
public sealed class Proxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly int _upstream;
    private readonly X509Certificate2? _cert;
    private readonly X509Certificate2Collection? _sent;
    private readonly Task? _gate;
    private readonly bool _reset;
    private readonly List<Task> _tasks = [];
    private readonly List<TcpClient> _clients = [];
    private readonly Task _accept;

    public int Port { get; }

    /// <summary>`sent`: the issuers to send with the certificate, as a server with a chain does.</summary>
    public Proxy(int upstreamPort, X509Certificate2? cert = null, Task? gate = null, bool reset = false,
        X509Certificate2Collection? sent = null)
    {
        _upstream = upstreamPort;
        _cert = cert;
        _sent = sent;
        _gate = gate;
        _reset = reset;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_tasks)
                {
                    _clients.Add(client);
                    _tasks.Add(Task.Run(() => ServeAsync(client)));
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            if (_gate is not null) await _gate.WaitAsync(_stop.Token);
            if (_reset)
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
                client.Client.Close(0);
                return;
            }
            Stream front = client.GetStream();
            if (_cert is not null)
            {
                var ssl = new SslStream(front);
                await ssl.AuthenticateAsServerAsync(_sent is null
                    ? new SslServerAuthenticationOptions { ServerCertificate = _cert }
                    : new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = SslStreamCertificateContext.Create(_cert, _sent, offline: true),
                    }, _stop.Token);
                front = ssl;
            }
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, _upstream, _stop.Token);
            var back = upstream.GetStream();
            await Task.WhenAny(front.CopyToAsync(back, _stop.Token), back.CopyToAsync(front, _stop.Token));
        }
        catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException
                                      or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The server dropped the connection, for example after rejecting the certificate.
        }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Task[] tasks;
        lock (_tasks) tasks = [.. _tasks];
        foreach (var client in _clients) client.Dispose();
        await Task.WhenAll(tasks.Append(_accept));
        _stop.Dispose();
    }
}

/// <summary>A raw engine client on the fake host, to hold a device name.</summary>
public sealed class HostHolder : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly Connection _connection;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pump = Task.CompletedTask;

    private HostHolder(string device) =>
        _connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC", DeviceName = device });

    /// <summary>Connects and waits for the sign-on screen, so the device is taken.</summary>
    public static async Task<HostHolder> ConnectAsync(FakeIbmI host, string device)
    {
        var holder = new HostHolder(device);
        await holder._tcp.ConnectAsync(IPAddress.Loopback, host.Port, T.Token());
        holder._pump = Task.Run(holder.PumpAsync);
        await holder._ready.Task.WaitAsync(T.Timeout);
        return holder;
    }

    private async Task PumpAsync()
    {
        var stream = _tcp.GetStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                var result = _connection.Receive(buffer.AsSpan(0, n));
                foreach (var send in result.Send) await stream.WriteAsync(send.Bytes);
                if (result.Events.Any(e => e is ConnectionEvent.Screen)) _ready.TrySetResult();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tcp.Close();
        await _pump;
    }
}
