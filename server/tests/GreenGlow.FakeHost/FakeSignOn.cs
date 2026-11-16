// A scripted fake of the IBM i sign-on server (as-signon, server ID E009) and of its port
// mapper (as-svrmap), for automated tests: exchange attributes, retrieve sign-on information
// and change password, against FakeAccounts. It never logs a password or a substitute.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GreenGlow.Engine;

namespace GreenGlow.FakeHost;

/// <summary>A small TCP server: accepts connections and runs each until it ends or the server is disposed.</summary>
public abstract class FakeTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly List<Task> _tasks = [];
    private readonly HashSet<TcpClient> _clients = [];
    private readonly Task _acceptLoop;
    private int _nextId;

    protected FakeTcpServer(int port, IPAddress? address, Action<string>? log)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        _listener.Start();
        Log = log ?? (_ => { });
        _acceptLoop = AcceptLoopAsync();
    }

    protected CancellationTokenSource Stop { get; } = new();

    protected Action<string> Log { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    protected abstract Task RunAsync(NetworkStream stream, int id);

    private async Task AcceptLoopAsync()
    {
        while (!Stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(Stop.Token);
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or OperationCanceledException)
            {
                return;
            }
            var id = Interlocked.Increment(ref _nextId);
            lock (_gate)
            {
                _clients.Add(client);
                _tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await RunAsync(client.GetStream(), id);
                    }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException
                                                  or OperationCanceledException or EndOfStreamException or InvalidDataException)
                    {
                    }
                    finally
                    {
                        lock (_gate) _clients.Remove(client);
                        client.Close();
                    }
                }));
            }
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop.Cancel();
        _listener.Stop();
        TcpClient[] clients;
        lock (_gate) clients = [.. _clients];
        foreach (var client in clients) client.Close();
        await _acceptLoop;
        Task[] tasks;
        lock (_gate) tasks = [.. _tasks];
        await Task.WhenAll(tasks);
        Stop.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>The fake port mapper: answers a service name with '+' and its port, or '-' for one it doesn't know.</summary>
public sealed class FakePortMapper(int port = 0, Action<string>? log = null, IPAddress? address = null)
    : FakeTcpServer(port, address, log)
{
    private readonly Dictionary<string, int> _services = [];
    private int _lookups;

    /// <summary>The services it knows, by the name asked for: as-signon, as-netprt, and with -s for TLS.</summary>
    public FakePortMapper Map(string service, int servicePort)
    {
        lock (_services) _services[service] = servicePort;
        return this;
    }

    public int Lookups => Volatile.Read(ref _lookups);

    protected override async Task RunAsync(NetworkStream stream, int id)
    {
        // The request is the name and nothing else: take what arrives in a moment.
        var buffer = new byte[64];
        var length = await stream.ReadAsync(buffer, Stop.Token);
        using (var more = CancellationTokenSource.CreateLinkedTokenSource(Stop.Token))
        {
            more.CancelAfter(TimeSpan.FromMilliseconds(50));
            try
            {
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), more.Token);
                    if (read == 0) break;
                    length += read;
                }
            }
            catch (OperationCanceledException) when (!Stop.IsCancellationRequested)
            {
            }
        }
        var name = Encoding.ASCII.GetString(buffer, 0, length);
        Interlocked.Increment(ref _lookups);
        int found;
        lock (_services) found = _services.GetValueOrDefault(name);
        var reply = new byte[5];
        reply[0] = (byte)(found > 0 ? '+' : '-');
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(1), found);
        await stream.WriteAsync(reply, Stop.Token);
        Log($"port mapper: {name} -> {(found > 0 ? found.ToString() : "unknown")}");
    }
}

/// <summary>The fake sign-on server: each connection exchanges attributes, then answers requests until it closes.</summary>
public sealed class FakeSignOn(FakeAccounts accounts, int port = 0, Action<string>? log = null, IPAddress? address = null)
    : FakeTcpServer(port, address, log)
{
    private const ushort ServerId = 0xE009;
    private int _verified;
    private int _refusals;
    private int _changes;

    public FakeAccounts Accounts { get; } = accounts;

    /// <summary>When set, every sign-on and password change answers with this return code.</summary>
    public int? Refuse { get; set; }

    /// <summary>Passwords accepted, refused, and changed so far.</summary>
    public int Verified => Volatile.Read(ref _verified);

    public int Refusals => Volatile.Read(ref _refusals);

    public int Changes => Volatile.Read(ref _changes);

    protected override async Task RunAsync(NetworkStream stream, int id)
    {
        var exchange = await ReadFrameAsync(stream, Stop.Token);
        Require(exchange, 0x7003);
        var asked = CodePoints(exchange, 20);
        if (!asked.TryGetValue(0x1103, out var clientSeed) || clientSeed.Length != 8) throw new InvalidDataException("no client seed");
        var serverSeed = RandomNumberGenerator.GetBytes(8);
        var level = Accounts.PasswordLevel;
        await stream.WriteAsync(Frame(0xF003, 0,
            Point(0x1101, [0, 7, 5, 0]), Point(0x1102, [0, 10]), Point(0x1103, serverSeed), Point(0x1119, [(byte)level]),
            Point(0x111F, Ebcdic.Encode("QZSOSIGN"))), Stop.Token);

        while (true)
        {
            var request = await ReadFrameAsync(stream, Stop.Token);
            var requestId = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(18));
            if (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6)) != ServerId) throw new InvalidDataException("another server");
            var scheme = request[20];
            var points = CodePoints(request, 21);
            var user = Ebcdic.Decode(points.GetValueOrDefault(0x1104) ?? []).TrimEnd();
            var substitute = points.GetValueOrDefault(0x1105) ?? [];
            switch (requestId)
            {
                case 0x7004:
                {
                    var rc = Refuse ?? Accounts.Check(user, scheme, level, substitute, clientSeed, serverSeed);
                    Count(rc, ref _verified);
                    Log($"sign-on #{id}: {user} {(rc == 0 ? "verified" : $"refused ({rc:X8})")}");
                    await stream.WriteAsync(rc != 0
                        ? Frame(0xF004, rc)
                        : Frame(0xF004, 0,
                            Point(0x1106, [0x07, 0xEA, 11, 16, 9, 30, 0, 0]), Point(0x1107, [0x07, 0xEA, 11, 13, 17, 5, 0, 0]),
                            Point(0x1108, [0x07, 0xEB, 2, 14, 0, 0, 0, 0]), Point(0x1114, [0, 0, 0, 37]),
                            Point(0x112C, [0, 0, 0, 7])), Stop.Token);
                    break;
                }
                case 0x7005:
                {
                    var rc = Refuse ?? Accounts.Change(user, scheme, level, substitute,
                        points.GetValueOrDefault(0x110C) ?? [], points.GetValueOrDefault(0x110D) ?? [],
                        Int(points.GetValueOrDefault(0x111C)), Int(points.GetValueOrDefault(0x111D)), clientSeed, serverSeed);
                    Count(rc, ref _changes);
                    Log($"sign-on #{id}: {user} password {(rc == 0 ? "changed" : $"change refused ({rc:X8})")}");
                    await stream.WriteAsync(Frame(0xF005, rc), Stop.Token);
                    break;
                }
                default:
                    throw new InvalidDataException($"request {requestId:X4}");
            }
        }
    }

    private void Count(int rc, ref int accepted)
    {
        if (rc == 0) Interlocked.Increment(ref accepted);
        else Interlocked.Increment(ref _refusals);
    }

    private static int Int(byte[]? data) => data is { Length: 4 } ? BinaryPrimitives.ReadInt32BigEndian(data) : 0;

    private static byte[] Point(ushort id, byte[] data)
    {
        var point = new byte[6 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(point, point.Length);
        BinaryPrimitives.WriteUInt16BigEndian(point.AsSpan(4), id);
        data.CopyTo(point, 6);
        return point;
    }

    /// <summary>A reply: the header, the return code as the template, then the code points.</summary>
    private static byte[] Frame(ushort replyId, int rc, params byte[][] points)
    {
        var frame = new byte[24 + points.Sum(p => p.Length)];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), ServerId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), replyId);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(20), rc);
        var at = 24;
        foreach (var point in points)
        {
            point.CopyTo(frame, at);
            at += point.Length;
        }
        return frame;
    }

    private static Dictionary<int, byte[]> CodePoints(byte[] frame, int at)
    {
        var points = new Dictionary<int, byte[]>();
        while (at + 6 <= frame.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(at));
            if (length < 6 || at + length > frame.Length) throw new InvalidDataException("bad code point");
            points[BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(at + 4))] = frame.AsSpan(at + 6, length - 6).ToArray();
            at += length;
        }
        return points;
    }

    private static void Require(byte[] frame, ushort requestId)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6)) != ServerId
            || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18)) != requestId)
            throw new InvalidDataException($"expected request {requestId:X4}");
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancel)
    {
        var header = new byte[20];
        await stream.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 20 || length > 1 << 20) throw new InvalidDataException("bad length");
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(20), cancel);
        return frame;
    }
}
