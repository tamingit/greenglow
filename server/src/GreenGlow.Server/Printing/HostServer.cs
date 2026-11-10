using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Printing;

/// <summary>One IBM i host server: its server ID and default ports (plain and TLS).</summary>
public sealed record HostService(string Name, ushort ServerId, int Port, int SecurePort)
{
    public static readonly HostService NetworkPrint = new("as-netprt", 0xE003, 8474, 9474);

    /// <summary>Where this service is for a Telnet connection: the same host, TLS when the connection is.</summary>
    public Target EndpointFor(Target telnet) => telnet with { Port = telnet.Secure ? SecurePort : Port };
}

public enum SignOnFailure
{
    /// <summary>The user or password can't be sent as typed; nothing reached the IBM i.</summary>
    InvalidUser,
    InvalidPassword,
    UnknownUser,
    WrongPassword,
    /// <summary>The password was wrong and the IBM i disabled the profile.</summary>
    WrongPasswordDisabled,
    ProfileDisabled,
    PasswordExpired,
    NoPassword,
    /// <summary>Any other refusal: the code is in the message.</summary>
    Refused,
    Unsupported,
    Unreachable,
    Protocol,
}

/// <summary>A host server sign-on that didn't succeed, with a message for the user.</summary>
public sealed class SignOnException(SignOnFailure failure, string message, int code = 0) : Exception(message)
{
    public SignOnFailure Failure { get; } = failure;

    /// <summary>The IBM i's return code, when it gave one.</summary>
    public int Code { get; } = code;
}

/// <summary>
/// A connection to an IBM i host server, signed on as one user, the way IBM's Toolbox for
/// Java and ACS connect. Every datastream starts with a 20-byte header (all
/// big-endian): total length (4), header ID (2), server ID (2), CS instance (4), correlation ID
/// (4), template length (2), request or reply ID (2); then the template, then code points
/// (length 4, counting itself, ID 2, data). Signing on is a random seed exchange (7001), whose
/// reply carries the password level in header byte 5, then Start Server (7002) with the user ID
/// and a password substitute. The password itself never crosses the network.
/// </summary>
public sealed partial class HostServerClient : IAsyncDisposable
{
    public static readonly TimeSpan SignOnTimeout = TimeSpan.FromSeconds(30);
    public const int HeaderLength = 20;

    /// <summary>Larger replies are refused rather than buffered.</summary>
    public const int MaxFrame = 32 * 1024 * 1024;

    private const ushort SeedRequest = 0x7001;
    private const ushort SeedReply = 0xF001;
    private const ushort StartRequest = 0x7002;
    private const ushort StartReply = 0xF002;
    private const ushort PasswordCodePoint = 0x1105;
    private const ushort UserIdCodePoint = 0x1104;

    /// <summary>Client attributes in the seed request: the client can do SHA-1 password substitutes.</summary>
    private const byte ShaCapable = 0x01;

    /// <summary>Client attributes in Start Server: the client takes job information back.</summary>
    private const byte JobInfo = 0x02;

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private int _correlation;

    private HostServerClient(TcpClient client, Stream stream, HostService service, string user, int passwordLevel)
    {
        _client = client;
        _stream = stream;
        Service = service;
        User = user;
        PasswordLevel = passwordLevel;
    }

    public HostService Service { get; }

    /// <summary>The IBM i user signed on, upper case.</summary>
    public string User { get; }

    /// <summary>The IBM i's QPWDLVL, as the seed exchange reported it.</summary>
    public int PasswordLevel { get; }

    [GeneratedRegex("^[A-Z@#$][A-Z0-9@#$_]{0,9}$")]
    private static partial Regex UserPattern();

    /// <summary>
    /// Connects to `endpoint` (from <see cref="HostService.EndpointFor"/>, with the Telnet
    /// connection's TLS trust) and signs on. Tries once: a refused sign-on counts toward the
    /// profile's QMAXSIGN, so it is never repeated here. The password is used for the substitute
    /// and not kept.
    /// </summary>
    public static async Task<HostServerClient> SignOnAsync(HostConnector connector, Target endpoint, HostService service,
        string user, string password, CancellationToken cancel)
    {
        user = user.Trim().ToUpperInvariant();
        if (!UserPattern().IsMatch(user))
            throw new SignOnException(SignOnFailure.InvalidUser, "That isn't an IBM i user name.");
        if (string.IsNullOrWhiteSpace(password)) throw new SignOnException(SignOnFailure.InvalidPassword, "Type the password.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(SignOnTimeout);
        TcpClient client;
        Stream stream;
        try
        {
            (client, stream) = await connector.ConnectAsync(endpoint, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or IOException or AuthenticationException
                                      || e is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new SignOnException(SignOnFailure.Unreachable, $"Couldn't reach the IBM i's {service.Name} server ({Describe(e)}).");
        }

        try
        {
            var clientSeed = RandomNumberGenerator.GetBytes(8);
            await stream.WriteAsync(Frame(service.ServerId, SeedRequest, clientSeed, [], ShaCapable), timeout.Token);
            var seed = await ReadFrameAsync(stream, timeout.Token);
            Expect(seed, SeedReply, 32);
            var rc = BinaryPrimitives.ReadInt32BigEndian(seed.AsSpan(20));
            if (rc != 0) throw Refusal(rc);
            var serverSeed = seed.AsSpan(24, 8).ToArray();
            var level = seed[5];

            var (scheme, substitute) = PasswordSubstitute.For(level, user, password, clientSeed, serverSeed);
            var start = Frame(service.ServerId, StartRequest, [scheme, 0x01],
                [CodePoint(PasswordCodePoint, substitute), CodePoint(UserIdCodePoint, PasswordSubstitute.UserIdEbcdic(user))], JobInfo);
            CryptographicOperations.ZeroMemory(substitute);
            await stream.WriteAsync(start, timeout.Token);
            var reply = await ReadFrameAsync(stream, timeout.Token);
            Expect(reply, StartReply, 24);
            rc = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20));
            if (rc != 0) throw Refusal(rc);
            return new HostServerClient(client, stream, service, user, level);
        }
        catch (Exception e)
        {
            await stream.DisposeAsync();
            client.Dispose();
            if (e is SignOnException) throw;
            if (e is OperationCanceledException && cancel.IsCancellationRequested) throw;
            if (e is IOException or SocketException or OperationCanceledException or EndOfStreamException)
                throw new SignOnException(SignOnFailure.Unreachable, $"The IBM i's {service.Name} server closed the connection ({Describe(e)}).");
            throw;
        }
    }

    /// <summary>The next correlation ID for a request on this connection (never 0).</summary>
    public int NextCorrelation() => Interlocked.Increment(ref _correlation);

    public async Task SendAsync(byte[] frame, CancellationToken cancel) => await _stream.WriteAsync(frame, cancel);

    public Task<byte[]> ReceiveAsync(CancellationToken cancel) => ReadFrameAsync(_stream, cancel);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    /// <summary>A datastream: the header, the template, then each code point as given.</summary>
    public static byte[] Frame(ushort serverId, ushort requestId, ReadOnlySpan<byte> template, IReadOnlyList<byte[]> codePoints,
        byte headerByte = 0, int correlation = 0)
    {
        var length = HeaderLength + template.Length + codePoints.Sum(c => c.Length);
        var frame = new byte[length];
        var span = frame.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span, length);
        frame[4] = headerByte;
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], serverId);
        BinaryPrimitives.WriteInt32BigEndian(span[12..], correlation);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..], (ushort)template.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[18..], requestId);
        template.CopyTo(span[HeaderLength..]);
        var at = HeaderLength + template.Length;
        foreach (var codePoint in codePoints)
        {
            codePoint.CopyTo(span[at..]);
            at += codePoint.Length;
        }
        return frame;
    }

    /// <summary>A code point: length (counting the 6-byte prefix), ID, data.</summary>
    public static byte[] CodePoint(ushort id, ReadOnlySpan<byte> data)
    {
        var point = new byte[6 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(point, point.Length);
        BinaryPrimitives.WriteUInt16BigEndian(point.AsSpan(4), id);
        data.CopyTo(point.AsSpan(6));
        return point;
    }

    /// <summary>One whole datastream, by the length in its header.</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < HeaderLength || length > MaxFrame)
            throw new SignOnException(SignOnFailure.Protocol, $"The IBM i sent a datastream of {length} bytes.");
        if (header[6] != 0xE0)
            throw new SignOnException(SignOnFailure.Protocol, "The IBM i sent a datastream for another server.");
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(HeaderLength), cancel);
        return frame;
    }

    private static void Expect(byte[] frame, ushort replyId, int minimum)
    {
        var id = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18));
        if (id != replyId || frame.Length < minimum)
            throw new SignOnException(SignOnFailure.Protocol, $"The IBM i answered sign-on with reply {id:X4} instead of {replyId:X4}.");
    }

    /// <summary>What a refused seed exchange or Start Server means, for the user.</summary>
    public static SignOnException Refusal(int rc) => rc switch
    {
        0x00020001 => new(SignOnFailure.UnknownUser, "The IBM i doesn't know that user.", rc),
        0x00020002 => new(SignOnFailure.ProfileDisabled, "That IBM i user profile is disabled.", rc),
        0x0003000B => new(SignOnFailure.WrongPassword,
            "Wrong password. Each wrong password counts toward the profile being disabled.", rc),
        0x0003000C => new(SignOnFailure.WrongPasswordDisabled, "Wrong password, and the IBM i has now disabled the profile.", rc),
        0x0003000D => new(SignOnFailure.PasswordExpired,
            "The password has expired. Change it at the IBM i sign-on screen, then try again.", rc),
        0x00030010 => new(SignOnFailure.NoPassword, "That user profile has no password, so it can't sign on.", rc),
        0x00010007 => new(SignOnFailure.InvalidUser, "The IBM i says that user name isn't valid.", rc),
        0x00010008 => new(SignOnFailure.InvalidPassword, "The IBM i says the password isn't a valid length.", rc),
        _ => new(SignOnFailure.Refused, $"The IBM i refused the sign-on (code {rc:X8}).", rc),
    };

    private static string Describe(Exception e) => e switch
    {
        SocketException s => s.SocketErrorCode.ToString(),
        AuthenticationException => "TLS failed",
        OperationCanceledException => "timed out",
        EndOfStreamException => "connection closed",
        _ => e.GetType().Name,
    };
}
