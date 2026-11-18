// A scripted fake of the IBM i remote command server (as-rmtcmd, server ID E008), for automated
// tests: the host server sign-on against FakeAccounts, exchange attributes, and a call to one
// program, QSYS/QSYRUSRI (format USRI0300, *CURRENT), answered from the accounts' profiles. It
// is written from IBM's documented layouts on its own, not with the server's code, so each checks
// the other. It never logs a password or a substitute.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GreenGlow.Engine;

namespace GreenGlow.FakeHost;

public sealed class FakeRemoteCommand(FakeAccounts accounts, int port = 0, Action<string>? log = null, IPAddress? address = null)
    : FakeTcpServer(port, address, log)
{
    private const ushort ServerId = 0xE008;
    private int _calls;

    public FakeAccounts Accounts { get; } = accounts;

    /// <summary>When set, the call answers with this 2-byte return code (0500: the program couldn't be found).</summary>
    public ushort? RefuseCall { get; set; }

    /// <summary>When set, QSYRUSRI reports this exception ID in its error code (CPF2204: not authorized).</summary>
    public string? ApiError { get; set; }

    /// <summary>QSYRUSRI calls answered so far.</summary>
    public int Calls => Volatile.Read(ref _calls);

    protected override async Task RunAsync(NetworkStream stream, int id)
    {
        // Sign-on: the random seed exchange, then Start Server with a password substitute.
        var seedRequest = await ReadFrameAsync(stream, Stop.Token);
        Require(seedRequest, 0x7001);
        var clientSeed = seedRequest.AsSpan(20, 8).ToArray();
        var serverSeed = RandomNumberGenerator.GetBytes(8);
        var level = Accounts.PasswordLevel;
        await stream.WriteAsync(Frame(0xF001, [0, 0, 0, 0, .. serverSeed], (byte)level), Stop.Token);
        var start = await ReadFrameAsync(stream, Stop.Token);
        Require(start, 0x7002);
        var points = Points(start, 20 + BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(16)));
        var user = Ebcdic.Decode(points[0x1104]).TrimEnd();
        var rc = Accounts.Check(user, start[20], level, points[0x1105], clientSeed, serverSeed);
        await stream.WriteAsync(Frame(0xF002, [(byte)(rc >> 24), (byte)(rc >> 16), (byte)(rc >> 8), (byte)rc]), Stop.Token);
        if (rc != 0)
        {
            Log($"rmtcmd #{id} sign-on refused ({rc:X8})");
            return;
        }

        // Exchange attributes: template CCSID (4), NLV (4), version (4), datastream level (2).
        var exchange = await ReadFrameAsync(stream, Stop.Token);
        Require(exchange, 0x1001);
        if (BinaryPrimitives.ReadUInt16BigEndian(exchange.AsSpan(16)) != 14 || exchange.Length != 34)
            throw new InvalidDataException("exchange attributes isn't 14 bytes of template");
        var exchangeReply = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(exchangeReply.AsSpan(2), 37);
        Ebcdic.Encode("2924").CopyTo(exchangeReply, 6);
        BinaryPrimitives.WriteInt32BigEndian(exchangeReply.AsSpan(10), 0x00070500);
        BinaryPrimitives.WriteUInt16BigEndian(exchangeReply.AsSpan(14), 10);
        await stream.WriteAsync(Frame(0x8001, exchangeReply), Stop.Token);

        while (true)
        {
            var call = await ReadFrameAsync(stream, Stop.Token);
            Require(call, 0x1003);
            await stream.WriteAsync(Answer(call, user), Stop.Token);
        }
    }

    private byte[] Answer(byte[] call, string user)
    {
        var program = Ebcdic.Decode(call.AsSpan(20, 10)).TrimEnd();
        var library = Ebcdic.Decode(call.AsSpan(30, 10)).TrimEnd();
        var count = BinaryPrimitives.ReadUInt16BigEndian(call.AsSpan(41));
        var parameters = new List<(int MaxLength, ushort Usage, byte[] Data)>();
        for (int at = 43, i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(call.AsSpan(at));
            if (BinaryPrimitives.ReadUInt16BigEndian(call.AsSpan(at + 4)) != 0x1103) throw new InvalidDataException("not a parameter");
            parameters.Add((BinaryPrimitives.ReadInt32BigEndian(call.AsSpan(at + 6)), BinaryPrimitives.ReadUInt16BigEndian(call.AsSpan(at + 10)),
                call.AsSpan(at + 12, length - 12).ToArray()));
            at += length;
        }
        if (RefuseCall is { } refused) return Frame(0x8003, [(byte)(refused >> 8), (byte)refused, 0, 0]);
        if (program != "QSYRUSRI" || library != "QSYS" || parameters.Count != 5)
            return Frame(0x8003, [0x05, 0x00, 0, 0]);
        // Usages: 12 output, 11 input, 13 input and output.
        if (parameters.Select(p => p.Usage).SequenceEqual(new ushort[] { 12, 11, 11, 11, 13 }) is false)
            throw new InvalidDataException("unexpected parameter usages");
        var receiverLength = BinaryPrimitives.ReadInt32BigEndian(parameters[1].Data);
        var format = Ebcdic.Decode(parameters[2].Data);
        var profileName = Ebcdic.Decode(parameters[3].Data).TrimEnd();
        if (format != "USRI0300" || profileName != "*CURRENT" || receiverLength != parameters[0].MaxLength)
            throw new InvalidDataException("unexpected QSYRUSRI parameters");
        Interlocked.Increment(ref _calls);

        var error = new byte[parameters[4].MaxLength];
        parameters[4].Data.CopyTo(error, 0);
        var receiver = new byte[receiverLength];
        if (ApiError is { } exception)
        {
            BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(4), 15);
            Ebcdic.Encode(exception).CopyTo(error, 8);
        }
        else
        {
            var profile = Accounts.ProfileOf(user);
            var groupsAt = 720;
            var used = groupsAt + profile.Supplemental.Length * 10;
            BinaryPrimitives.WriteInt32BigEndian(receiver, used);
            BinaryPrimitives.WriteInt32BigEndian(receiver.AsSpan(4), used);
            Ebcdic.Encode(user.PadRight(10)).CopyTo(receiver, 8);
            string[] order = ["*ALLOBJ", "*SECADM", "*JOBCTL", "*SPLCTL", "*SAVSYS", "*SERVICE", "*AUDIT", "*IOSYSCFG"];
            Ebcdic.Encode(string.Concat(order.Select(a => profile.Authorities.Contains(a) ? "Y" : "N")).PadRight(15)).CopyTo(receiver, 83);
            Ebcdic.Encode((profile.Group ?? "*NONE").PadRight(10)).CopyTo(receiver, 98);
            BinaryPrimitives.WriteInt32BigEndian(receiver.AsSpan(584), groupsAt);
            BinaryPrimitives.WriteInt32BigEndian(receiver.AsSpan(588), profile.Supplemental.Length);
            for (var i = 0; i < profile.Supplemental.Length; i++)
                Ebcdic.Encode(profile.Supplemental[i].PadRight(10)).CopyTo(receiver, groupsAt + i * 10);
        }
        // Output parameters back in order, trailing zero bytes left off as the IBM i may.
        return Frame(0x8003, [0, 0, 0, 0, .. Output(receiver), .. Output(error)]);
    }

    private static byte[] Output(byte[] data)
    {
        var used = data.Length;
        while (used > 0 && data[used - 1] == 0) used--;
        var entry = new byte[12 + used];
        BinaryPrimitives.WriteInt32BigEndian(entry, entry.Length);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(4), 0x1103);
        BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(6), data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(10), 12);
        data.AsSpan(0, used).CopyTo(entry.AsSpan(12));
        return entry;
    }

    private static Dictionary<int, byte[]> Points(byte[] frame, int at)
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

    /// <summary>A reply: the header, then `body` (the template, and for a call its output parameters after the 4-byte template).</summary>
    private static byte[] Frame(ushort replyId, byte[] body, byte serverAttributes = 0)
    {
        var template = body;
        var frame = new byte[20 + template.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        frame[5] = serverAttributes;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), ServerId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)(replyId == 0x8003 ? 4 : template.Length));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), replyId);
        template.CopyTo(frame, 20);
        return frame;
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
