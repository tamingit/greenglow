using System.Buffers.Binary;
using System.Security.Cryptography;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.HostServers;

/// <summary>What the IBM i said about a user whose password it accepted. Dates are in the IBM i's own time.</summary>
public sealed record SignOnInfo(string User, int PasswordLevel, DateTime? LastSignOn, DateTime? PasswordExpires, int? ExpiryWarningDays);

/// <summary>
/// The IBM i's sign-on server (as-signon, server ID E009): checks a user and password, and
/// changes a password, the way IBM's Toolbox for Java and ACS do. Each call is one connection:
/// exchange attributes (7003; the reply has the server's seed and password level), then
/// retrieve sign-on information (7004) or change password (7005) with a password substitute.
/// No password crosses the network: a change sends both passwords XORed with further
/// substitutes. Each call tries once: a refused one counts toward the profile's QMAXSIGN.
/// </summary>
public static class SignOnServer
{
    private const ushort ExchangeRequest = 0x7003;
    private const ushort ExchangeReply = 0xF003;
    private const ushort InfoRequest = 0x7004;
    private const ushort InfoReply = 0xF004;
    private const ushort ChangeRequest = 0x7005;
    private const ushort ChangeReply = 0xF005;

    private const ushort ClientVersion = 0x1101;
    private const ushort ClientLevel = 0x1102;
    private const ushort Seed = 0x1103;
    private const ushort UserId = 0x1104;
    private const ushort Password = 0x1105;
    private const ushort CurrentSignOn = 0x1106;
    private const ushort LastSignOn = 0x1107;
    private const ushort Expiration = 0x1108;
    private const ushort ProtectedOld = 0x110C;
    private const ushort ProtectedNew = 0x110D;
    private const ushort ClientCcsid = 0x1113;
    private const ushort PasswordLevel = 0x1119;
    private const ushort OldLength = 0x111C;
    private const ushort NewLength = 0x111D;
    private const ushort ProtectedCcsid = 0x111E;
    private const ushort ExpirationWarning = 0x112C;

    /// <summary>The datastream level this client speaks: 8 is the one that knows password level 4.</summary>
    private const ushort Level = 8;

    /// <summary>Checks a user's password. Throws SignOnException when it isn't accepted (see its Failure).</summary>
    public static async Task<SignOnInfo> VerifyAsync(HostConnector connector, Target endpoint, string user, string password,
        CancellationToken cancel)
    {
        user = Checked(user, password);
        return await RunAsync(connector, endpoint, cancel, async (stream, exchange, token) =>
        {
            var (scheme, substitute) = PasswordSubstitute.For(exchange.PasswordLevel, user, password, exchange.ClientSeed, exchange.ServerSeed);
            var request = HostServerClient.Frame(HostService.SignOn.ServerId, InfoRequest, [scheme],
            [
                HostServerClient.CodePoint(ClientCcsid, Int(1200)),
                HostServerClient.CodePoint(Password, substitute),
                HostServerClient.CodePoint(UserId, PasswordSubstitute.UserIdEbcdic(user)),
            ]);
            CryptographicOperations.ZeroMemory(substitute);
            await stream.WriteAsync(request, token);
            var reply = await HostServerClient.ReadFrameAsync(stream, token);
            HostServerClient.Expect(reply, InfoReply, 24);
            var rc = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20));
            if (rc != 0) throw HostServerClient.Refusal(rc);
            var points = CodePoints(reply, 24);
            return new SignOnInfo(user, exchange.PasswordLevel, Date(points, LastSignOn), Date(points, Expiration),
                points.TryGetValue(ExpirationWarning, out var warning) && warning.Length >= 4
                    ? BinaryPrimitives.ReadInt32BigEndian(warning)
                    : null);
        });
    }

    /// <summary>
    /// Changes a user's password, given the current one (which may have expired). Throws
    /// SignOnException: WrongPassword and the like for the current one, NewPasswordRefused when
    /// the IBM i's password rules refuse the new one.
    /// </summary>
    public static async Task ChangePasswordAsync(HostConnector connector, Target endpoint, string user, string oldPassword,
        string newPassword, CancellationToken cancel)
    {
        user = Checked(user, oldPassword);
        if (string.IsNullOrWhiteSpace(newPassword)) throw new SignOnException(SignOnFailure.NewPasswordRefused, "Type the new password.");
        await RunAsync(connector, endpoint, cancel, async (stream, exchange, token) =>
        {
            var change = PasswordSubstitute.ForChange(exchange.PasswordLevel, user, oldPassword, newPassword,
                exchange.ClientSeed, exchange.ServerSeed);
            List<byte[]> points =
            [
                HostServerClient.CodePoint(UserId, PasswordSubstitute.UserIdEbcdic(user)),
                HostServerClient.CodePoint(Password, change.Substitute),
                HostServerClient.CodePoint(ProtectedOld, change.ProtectedOld),
                HostServerClient.CodePoint(ProtectedNew, change.ProtectedNew),
            ];
            if (change.Scheme != PasswordSubstitute.DesScheme)
            {
                points.Add(HostServerClient.CodePoint(OldLength, Int(change.OldLength)));
                points.Add(HostServerClient.CodePoint(NewLength, Int(change.NewLength)));
                points.Add(HostServerClient.CodePoint(ProtectedCcsid, Int(13488)));
            }
            await stream.WriteAsync(HostServerClient.Frame(HostService.SignOn.ServerId, ChangeRequest, [change.Scheme], points), token);
            var reply = await HostServerClient.ReadFrameAsync(stream, token);
            HostServerClient.Expect(reply, ChangeReply, 24);
            var rc = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20));
            if (rc != 0) throw HostServerClient.Refusal(rc);
            return true;
        });
    }

    /// <summary>
    /// The IBM i's password level (QPWDLVL), from an attribute exchange alone: no user, no
    /// password, nothing that counts as a sign-on. Telnet doesn't say which substitute an IBM
    /// i wants, so automatic sign-on asks here first, as ACS does.
    /// </summary>
    public static Task<int> PasswordLevelAsync(HostConnector connector, Target endpoint, CancellationToken cancel) =>
        RunAsync(connector, endpoint, cancel, (_, exchange, _) => Task.FromResult(exchange.PasswordLevel));

    private sealed record Exchange(byte[] ClientSeed, byte[] ServerSeed, int PasswordLevel);

    private static string Checked(string user, string password)
    {
        user = user.Trim().ToUpperInvariant();
        if (!HostServerClient.UserPattern().IsMatch(user))
            throw new SignOnException(SignOnFailure.InvalidUser, "That isn't an IBM i user name.");
        if (string.IsNullOrWhiteSpace(password)) throw new SignOnException(SignOnFailure.InvalidPassword, "Type the password.");
        return user;
    }

    /// <summary>Connects, exchanges attributes and runs one request; the connection is closed after it.</summary>
    private static async Task<T> RunAsync<T>(HostConnector connector, Target endpoint, CancellationToken cancel,
        Func<Stream, Exchange, CancellationToken, Task<T>> request)
    {
        var service = HostService.SignOn;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(HostServerClient.SignOnTimeout);
        var (client, stream) = await HostServerClient.OpenAsync(connector, endpoint, service, timeout.Token, cancel);
        try
        {
            var clientSeed = RandomNumberGenerator.GetBytes(8);
            await stream.WriteAsync(HostServerClient.Frame(service.ServerId, ExchangeRequest, [],
            [
                HostServerClient.CodePoint(ClientVersion, Int(1)),
                HostServerClient.CodePoint(ClientLevel, [(byte)(Level >> 8), (byte)Level]),
                HostServerClient.CodePoint(Seed, clientSeed),
            ]), timeout.Token);
            var reply = await HostServerClient.ReadFrameAsync(stream, timeout.Token);
            HostServerClient.Expect(reply, ExchangeReply, 24);
            var rc = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20));
            if (rc != 0) throw HostServerClient.Refusal(rc);
            var points = CodePoints(reply, 24);
            if (!points.TryGetValue(Seed, out var serverSeed) || serverSeed.Length != 8)
                throw new SignOnException(SignOnFailure.Protocol, "The IBM i's sign-on server sent no seed.");
            // Before password levels there was only level 0, and no code point for it.
            var level = points.TryGetValue(PasswordLevel, out var l) && l.Length >= 1 ? l[0] : 0;
            return await request(stream, new Exchange(clientSeed, serverSeed, level), timeout.Token);
        }
        catch (Exception e)
        {
            if (HostServerClient.Lost(e, service, cancel) is { } lost) throw lost;
            throw;
        }
        finally
        {
            await stream.DisposeAsync();
            client.Dispose();
        }
    }

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static Dictionary<ushort, byte[]> CodePoints(byte[] frame, int at)
    {
        var points = new Dictionary<ushort, byte[]>();
        while (at + 6 <= frame.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(at));
            if (length < 6 || length > frame.Length - at)
                throw new SignOnException(SignOnFailure.Protocol, "The IBM i's sign-on server sent a reply that can't be read.");
            points.TryAdd(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(at + 4)), frame.AsSpan(at + 6, length - 6).ToArray());
            at += length;
        }
        return points;
    }

    /// <summary>A date: year (2 bytes), month, day, hour, minute, second. Null when absent or not a date.</summary>
    private static DateTime? Date(Dictionary<ushort, byte[]> points, ushort id)
    {
        if (!points.TryGetValue(id, out var d) || d.Length < 7) return null;
        try
        {
            return new DateTime(BinaryPrimitives.ReadUInt16BigEndian(d), d[2], d[3], d[4], d[5], d[6], DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
