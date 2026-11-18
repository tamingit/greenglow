using System.Buffers.Binary;
using GreenGlow.Engine;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.HostServers;

/// <summary>
/// What the IBM i says about a user profile: its group profile, supplemental groups and special
/// authorities (such as *SECADM), all upper case. Read with the profile's own sign-on.
/// </summary>
public sealed record IbmProfile(string User, string? GroupProfile, IReadOnlyList<string> SupplementalGroups,
    IReadOnlyList<string> SpecialAuthorities)
{
    /// <summary>The group profile and the supplemental groups.</summary>
    public IEnumerable<string> Groups => GroupProfile is null ? SupplementalGroups : [GroupProfile, .. SupplementalGroups];
}

/// <summary>
/// The IBM i's remote command server (as-rmtcmd, server ID E008), used for one thing: calling
/// the Retrieve User Information API (QSYS/QSYRUSRI, format USRI0300) for the signed-on user
/// (*CURRENT), the way IBM's Toolbox for Java calls a program. After the host server sign-on,
/// the client exchanges attributes (1001; the reply, 8001, has the server's datastream level),
/// then calls the program (1003): a template with the program and library names (10 bytes each,
/// EBCDIC), a message option and the parameter count, then each parameter as code point 1103:
/// length (4), ID (2), the most bytes it may hold (4), its usage (2: 11 input, 12 output, 13
/// both) and the input bytes. The reply (8003) has a 2-byte return code, then the output
/// parameters in order, laid out the same way; the server may leave off trailing zero bytes.
/// </summary>
public static class RemoteCommand
{
    private const ushort ExchangeRequest = 0x1001;
    private const ushort ExchangeReply = 0x8001;
    private const ushort CallRequest = 0x1003;
    private const ushort CallReply = 0x8003;
    private const ushort Parameter = 0x1103;
    private const ushort Input = 11;
    private const ushort Output = 12;
    private const ushort InOut = 13;

    /// <summary>No messages back: a failure is reported by its return code and the API's error code.</summary>
    private const byte NoMessages = 1;

    /// <summary>The receiver for USRI0300: the fixed part, then up to 15 supplemental groups of 10 bytes.</summary>
    internal const int ReceiverLength = 2048;

    /// <summary>The error code parameter (ERRC0100): bytes provided, bytes available, exception ID.</summary>
    private const int ErrorCodeLength = 16;

    /// <summary>The special authorities in USRI0300's 15 Y/N flags, in order; the rest are reserved.</summary>
    public static readonly IReadOnlyList<string> SpecialAuthorityNames =
        ["*ALLOBJ", "*SECADM", "*JOBCTL", "*SPLCTL", "*SAVSYS", "*SERVICE", "*AUDIT", "*IOSYSCFG"];

    /// <summary>
    /// Signs on to the remote command server as the user, and retrieves their profile's groups and
    /// special authorities. The password is used for the substitute and not kept. Throws
    /// SignOnException: Unreachable, Protocol, or Refused when the IBM i refuses the call.
    /// </summary>
    public static async Task<IbmProfile> CurrentUserAsync(HostConnector connector, Target endpoint, string user, string password,
        CancellationToken cancel)
    {
        await using var client = await HostServerClient.SignOnAsync(connector, endpoint, HostService.RemoteCommand, user, password, cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(HostServerClient.SignOnTimeout);
        try
        {
            await client.SendAsync(ExchangeFrame(), timeout.Token);
            var exchange = await client.ReceiveAsync(timeout.Token);
            Expect(exchange, ExchangeReply, 22);
            var rc = BinaryPrimitives.ReadUInt16BigEndian(exchange.AsSpan(20));
            // 0104 to 0108: a CCSID or language it doesn't take; it carries on with its own.
            if (rc is not (0 or 0x0100 or (>= 0x0104 and <= 0x0108)))
                throw new SignOnException(SignOnFailure.Refused, $"The IBM i's remote command server refused the client (code {rc:X4}).", rc);

            await client.SendAsync(CallFrame("QSYRUSRI", "QSYS",
            [
                (Output, ReceiverLength, []),
                (Input, 4, Int(ReceiverLength)),
                (Input, 8, Ebcdic.Encode("USRI0300")),
                (Input, 10, Ebcdic.Encode("*CURRENT".PadRight(10))),
                (InOut, ErrorCodeLength, [.. Int(ErrorCodeLength), .. new byte[ErrorCodeLength - 4]]),
            ]), timeout.Token);
            var reply = await client.ReceiveAsync(timeout.Token);
            Expect(reply, CallReply, 22);
            rc = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20));
            if (rc != 0)
                throw new SignOnException(SignOnFailure.Refused, $"The IBM i couldn't run QSYRUSRI (code {rc:X4}).", rc);
            var outputs = Outputs(reply, [ReceiverLength, ErrorCodeLength]);
            var error = outputs[1];
            if (BinaryPrimitives.ReadInt32BigEndian(error.AsSpan(4)) != 0)
                throw new SignOnException(SignOnFailure.Refused,
                    $"QSYRUSRI refused: {Ebcdic.Decode(error.AsSpan(8, 7)).Trim()}.");
            return Parse(outputs[0]);
        }
        catch (Exception e)
        {
            if (HostServerClient.Lost(e, HostService.RemoteCommand, cancel) is { } lost) throw lost;
            throw;
        }
    }

    /// <summary>Exchange attributes: client CCSID 37, language 2924 (English), client version 1.</summary>
    internal static byte[] ExchangeFrame()
    {
        var template = new byte[14];
        BinaryPrimitives.WriteInt32BigEndian(template, 37);
        Ebcdic.Encode("2924").CopyTo(template, 4);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(8), 1);
        return HostServerClient.Frame(HostService.RemoteCommand.ServerId, ExchangeRequest, template, []);
    }

    internal static byte[] CallFrame(string program, string library, IReadOnlyList<(ushort Usage, int MaxLength, byte[] Data)> parameters)
    {
        var template = new byte[23];
        Ebcdic.Encode(program.PadRight(10)).CopyTo(template, 0);
        Ebcdic.Encode(library.PadRight(10)).CopyTo(template, 10);
        template[20] = NoMessages;
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(21), (ushort)parameters.Count);
        var points = parameters.Select(p =>
        {
            var data = new byte[6 + p.Data.Length];
            BinaryPrimitives.WriteInt32BigEndian(data, p.MaxLength);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), p.Usage);
            p.Data.CopyTo(data, 6);
            return HostServerClient.CodePoint(Parameter, data);
        }).ToList();
        return HostServerClient.Frame(HostService.RemoteCommand.ServerId, CallRequest, template, points);
    }

    /// <summary>The output parameters, in order, each zero-filled to its length.</summary>
    internal static byte[][] Outputs(byte[] reply, int[] lengths)
    {
        var outputs = new byte[lengths.Length][];
        var at = 24;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (at + 12 > reply.Length) throw Unreadable();
            var length = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at));
            var id = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 4));
            var usage = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 10));
            // 22 and 23 are run-length compressed, which this client never asks for.
            if (id != Parameter || length < 12 || at + length > reply.Length || length - 12 > lengths[i] || usage is 22 or 23)
                throw Unreadable();
            outputs[i] = new byte[lengths[i]];
            reply.AsSpan(at + 12, length - 12).CopyTo(outputs[i]);
            at += length;
        }
        return outputs;
    }

    /// <summary>USRI0300: the profile name (8), special authorities (83), group profile (98), and the supplemental groups (584, 588).</summary>
    internal static IbmProfile Parse(byte[] receiver)
    {
        var returned = BinaryPrimitives.ReadInt32BigEndian(receiver);
        if (returned < 592 || returned > receiver.Length) throw Unreadable();
        var user = Ebcdic.Decode(receiver.AsSpan(8, 10)).Trim();
        var flags = receiver.AsSpan(83, SpecialAuthorityNames.Count).ToArray();
        var authorities = SpecialAuthorityNames.Where((_, i) => flags[i] == 0xE8).ToList(); // 'Y'
        var group = Ebcdic.Decode(receiver.AsSpan(98, 10)).Trim();
        var offset = BinaryPrimitives.ReadInt32BigEndian(receiver.AsSpan(584));
        var count = BinaryPrimitives.ReadInt32BigEndian(receiver.AsSpan(588));
        var supplemental = new List<string>();
        if (count > 0)
        {
            if (count > 15 || offset < 592 || offset + count * 10 > returned) throw Unreadable();
            for (var i = 0; i < count; i++)
                supplemental.Add(Ebcdic.Decode(receiver.AsSpan(offset + i * 10, 10)).Trim());
        }
        return new IbmProfile(user, group is "" or "*NONE" ? null : group, supplemental, authorities);
    }

    private static void Expect(byte[] frame, ushort replyId, int minimum)
    {
        var id = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18));
        if (id != replyId || frame.Length < minimum)
            throw new SignOnException(SignOnFailure.Protocol, $"The IBM i's remote command server answered {id:X4} instead of {replyId:X4}.");
    }

    private static SignOnException Unreadable() =>
        new(SignOnFailure.Protocol, "The IBM i's remote command server sent a reply that can't be read.");

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
