using System.Buffers.Binary;
using System.Globalization;
using GreenGlow.Engine;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Printing;

/// <summary>
/// One of the user's spooled files, as the Network Print server lists it. `Identity` is the
/// spooled file ID code point exactly as the server sent it, which opens the file again; it is
/// never shown to a browser.
/// </summary>
public sealed record SpooledFile(
    string Name, int Number, string JobName, string JobUser, string JobNumber, string JobSystem,
    DateTime? Created, string Status, int Pages, string UserData, string DeviceType, string OutputQueue, string FormType)
{
    internal byte[] Identity { get; init; } = [];
}

/// <summary>A Network Print server request that failed, with a message for the user.</summary>
public sealed class PrintServerException(string message, int code = 0, bool lost = false) : Exception(message)
{
    /// <summary>The server's return code, when it gave one.</summary>
    public int Code { get; } = code;

    /// <summary>The connection is gone: sign on again.</summary>
    public bool Lost { get; } = lost;
}

/// <summary>
/// The IBM i Network Print server (as-netprt), signed on as the user: lists the
/// user's own spooled files and reads one. After the 20-byte host server header each request
/// has a 12-byte template (action 2, flags 4, return code 2, 4 more) and its code points; the
/// request ID is the kind of object (spooled file 0001, the server itself 0008). A request may
/// have several replies with its correlation ID; the high bit of the flags is set on all but the
/// last. Attribute lists are code points of their own shape (see <see cref="NpAttributes"/>).
/// Layouts follow IBM's Toolbox for Java (NPDataStream, NPCodePoint, NPCPAttributeValue,
/// NPConversation, PrintObjectListImplRemote, PrintObjectInputStreamImplRemote). One request at
/// a time.
/// </summary>
public sealed class NetworkPrintClient : IAsyncDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Larger spooled files are refused rather than read into memory.</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;

    private const int ReadChunk = 64 * 1024;
    private const int ClientCcsid = 13488;

    // Objects (the request ID).
    private const ushort SpooledFileObject = 0x0001;
    private const ushort ServerObject = 0x0008;

    // Actions.
    private const ushort Open = 0x0002;
    private const ushort Read = 0x0003;
    private const ushort Close = 0x0005;
    private const ushort RetrieveAttributes = 0x000F;
    private const ushort List = 0x0016;

    // Code points.
    private const ushort SpooledFileId = 0x0001;
    private const ushort Selection = 0x0006;
    private const ushort AttributeList = 0x0007;
    private const ushort AttributeValues = 0x0008;
    private const ushort Data = 0x0009;
    private const ushort SpooledFileHandle = 0x000C;

    // Return codes.
    private const int NotAuthorized = 0x0008;
    private const int CpfMessage = 0x0009;
    private const int ReadIncomplete = 0x0012;
    private const int EndOfFile = 0x0013;
    private const int EmptyList = 0x0014;
    private const int NotFound = 0x001E;

    private readonly HostServerClient _server;
    private readonly SemaphoreSlim _one = new(1, 1);

    private NetworkPrintClient(HostServerClient server, int ccsid)
    {
        _server = server;
        ServerCcsid = ccsid;
    }

    /// <summary>The IBM i user signed on, upper case.</summary>
    public string User => _server.User;

    /// <summary>The CCSID the server reported. Text is read as CCSID 37 whatever it is (QCHRID 697 37).</summary>
    public int ServerCcsid { get; }

    /// <summary>
    /// Signs on to the Network Print server for a Telnet connection (its host, TLS when it is)
    /// and exchanges CCSIDs. The password is used for the sign-on only; see
    /// <see cref="HostServerClient.SignOnAsync"/>.
    /// </summary>
    public static async Task<NetworkPrintClient> ConnectAsync(HostConnector connector, Target endpoint, string user, string password,
        CancellationToken cancel)
    {
        var server = await HostServerClient.SignOnAsync(connector, endpoint, HostService.NetworkPrint, user, password, cancel);
        try
        {
            var replies = await ExchangeAsync(server, ServerObject, RetrieveAttributes,
                [(AttributeValues, NpAttributes.Encode([(NpAttribute.ServerCcsid, ClientCcsid)])),
                 (AttributeList, NpAttributes.EncodeIds([NpAttribute.ServerCcsid]))], cancel);
            var reply = replies[^1];
            if (reply.ReturnCode != 0) throw Failure(reply, "start the print server conversation");
            var ccsid = reply.CodePoints.TryGetValue(AttributeValues, out var values)
                        && NpAttributes.Decode(values).TryGetValue(NpAttribute.ServerCcsid, out var value) && value is int c
                ? c
                : 37;
            return new NetworkPrintClient(server, ccsid);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>The signed-on user's spooled files on every output queue, in the server's order.</summary>
    public async Task<IReadOnlyList<SpooledFile>> ListAsync(CancellationToken cancel)
    {
        var replies = await RequestAsync(SpooledFileObject, List,
            [(Selection, NpAttributes.Encode([(NpAttribute.JobUser, "*CURRENT")])),
             (AttributeList, NpAttributes.EncodeIds(NpAttribute.Listed))], cancel);
        var files = new List<SpooledFile>();
        foreach (var reply in replies)
        {
            if (reply.ReturnCode == EmptyList) continue;
            if (reply.ReturnCode != 0) throw Failure(reply, "list your spooled files");
            if (!reply.CodePoints.TryGetValue(SpooledFileId, out var id)) continue;
            var attributes = NpAttributes.Decode(id);
            if (reply.CodePoints.TryGetValue(AttributeValues, out var values))
                foreach (var (key, value) in NpAttributes.Decode(values)) attributes.TryAdd(key, value);
            files.Add(ToSpooledFile(attributes) with { Identity = id });
        }
        return files;
    }

    /// <summary>A spooled file's whole data, as stored (SCS, AFPDS, ...).</summary>
    public async Task<byte[]> ReadAllAsync(SpooledFile file, CancellationToken cancel)
    {
        if (file.Identity.Length == 0) throw new ArgumentException("not a listed spooled file", nameof(file));
        var opened = (await RequestAsync(SpooledFileObject, Open,
            [(SpooledFileId, file.Identity), (Selection, NpAttributes.Encode([(NpAttribute.PrecomputeBytes, "*YES")]))], cancel))[^1];
        if (opened.ReturnCode != 0) throw Failure(opened, "open the spooled file");
        if (!opened.CodePoints.TryGetValue(SpooledFileHandle, out var handle))
            throw new PrintServerException("The IBM i opened the spooled file but sent no handle for it.");

        using var data = new MemoryStream();
        try
        {
            while (true)
            {
                var read = (await RequestAsync(SpooledFileObject, Read,
                    [(AttributeValues, NpAttributes.Encode([(NpAttribute.NumberOfBytes, ReadChunk)])), (SpooledFileHandle, handle)],
                    cancel))[^1];
                if (read.ReturnCode == EndOfFile) break;
                if (read.ReturnCode is not (0 or ReadIncomplete)) throw Failure(read, "read the spooled file");
                var chunk = read.CodePoints.GetValueOrDefault(Data) ?? [];
                if (data.Length + chunk.Length > MaxFileBytes)
                    throw new PrintServerException($"The spooled file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
                data.Write(chunk);
                if (chunk.Length == 0 && read.ReturnCode == 0) break;
            }
        }
        finally
        {
            if (!cancel.IsCancellationRequested)
            {
                try
                {
                    await RequestAsync(SpooledFileObject, Close, [(SpooledFileHandle, handle)], CancellationToken.None);
                }
                catch (PrintServerException)
                {
                    // The data is read; a failed close changes nothing for the user.
                }
            }
        }
        return data.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _one.Dispose();
    }

    private async Task<IReadOnlyList<NpReply>> RequestAsync(ushort objectType, ushort action,
        IReadOnlyList<(ushort Id, byte[] Data)> codePoints, CancellationToken cancel)
    {
        await _one.WaitAsync(cancel);
        try
        {
            return await ExchangeAsync(_server, objectType, action, codePoints, cancel);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Sends one request and reads its replies up to the last one.</summary>
    private static async Task<IReadOnlyList<NpReply>> ExchangeAsync(HostServerClient server, ushort objectType, ushort action,
        IReadOnlyList<(ushort Id, byte[] Data)> codePoints, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(RequestTimeout);
        var correlation = server.NextCorrelation();
        var template = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(template, action);
        var points = codePoints.OrderBy(c => c.Id).Select(c => HostServerClient.CodePoint(c.Id, c.Data)).ToList();
        var request = HostServerClient.Frame(HostService.NetworkPrint.ServerId, objectType, template, points, correlation: correlation);
        try
        {
            await server.SendAsync(request, timeout.Token);
            var replies = new List<NpReply>();
            while (true)
            {
                var reply = NpReply.Parse(await server.ReceiveAsync(timeout.Token));
                if (reply.Correlation != correlation)
                    throw new PrintServerException("The IBM i's print server answered another request.", lost: true);
                replies.Add(reply);
                if (reply.Last) return replies;
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or SignOnException
                                      || e is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new PrintServerException(e is OperationCanceledException
                ? "The IBM i's print server didn't answer."
                : "The connection to the IBM i's print server was lost.", lost: true);
        }
    }

    private static PrintServerException Failure(NpReply reply, string doing)
    {
        var message = reply.CodePoints.TryGetValue(AttributeValues, out var values) ? NpAttributes.Decode(values) : [];
        var text = message.GetValueOrDefault(NpAttribute.MessageText) as string;
        var id = message.GetValueOrDefault(NpAttribute.MessageId) as string;
        return reply.ReturnCode switch
        {
            CpfMessage when !string.IsNullOrWhiteSpace(text) =>
                new PrintServerException(string.IsNullOrWhiteSpace(id) ? text : $"{id}: {text}", reply.ReturnCode),
            NotAuthorized => new PrintServerException("You aren't authorized to that spooled file.", reply.ReturnCode),
            NotFound => new PrintServerException("That spooled file no longer exists. Refresh the list.", reply.ReturnCode),
            _ => new PrintServerException($"Couldn't {doing} (the print server's code {reply.ReturnCode:X4}).", reply.ReturnCode),
        };
    }

    private static SpooledFile ToSpooledFile(Dictionary<ushort, object> a)
    {
        string S(ushort id) => a.GetValueOrDefault(id) as string ?? "";
        int I(ushort id) => a.GetValueOrDefault(id) is int i ? i : 0;
        var queue = S(NpAttribute.OutputQueue);
        var library = S(NpAttribute.OutputQueueLibrary);
        return new SpooledFile(S(NpAttribute.SpooledFileName), I(NpAttribute.SpooledFileNumber), S(NpAttribute.JobName),
            S(NpAttribute.JobUser), S(NpAttribute.JobNumber), S(NpAttribute.JobSystem),
            Created(S(NpAttribute.Date), S(NpAttribute.Time)), S(NpAttribute.Status), I(NpAttribute.Pages), S(NpAttribute.UserData),
            S(NpAttribute.DeviceType), library.Length > 0 && queue.Length > 0 ? $"{library}/{queue}" : queue, S(NpAttribute.FormType));
    }

    /// <summary>CYYMMDD (C is 0 for 19xx, 1 for 20xx) and HHMMSS, in the IBM i's own time.</summary>
    internal static DateTime? Created(string date, string time)
    {
        if (date.Length != 7 || !int.TryParse(date, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        var year = 1900 + (date[0] - '0') * 100 + int.Parse(date[1..3], CultureInfo.InvariantCulture);
        var text = $"{year:D4}{date[3..]}{(time.Length == 6 ? time : "000000")}";
        return DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
            ? created
            : null;
    }

    /// <summary>One reply: its template and code points.</summary>
    private sealed record NpReply(int Correlation, bool Last, int ReturnCode, Dictionary<ushort, byte[]> CodePoints)
    {
        public static NpReply Parse(byte[] frame)
        {
            var templateLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16));
            if (templateLength < 8 || HostServerClient.HeaderLength + templateLength > frame.Length)
                throw new PrintServerException("The IBM i's print server sent a reply too short to read.", lost: true);
            var template = frame.AsSpan(HostServerClient.HeaderLength, templateLength);
            var flags = BinaryPrimitives.ReadUInt32BigEndian(template[2..]);
            var rc = BinaryPrimitives.ReadUInt16BigEndian(template[6..]);
            var points = new Dictionary<ushort, byte[]>();
            var at = HostServerClient.HeaderLength + templateLength;
            while (at + 6 <= frame.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(at));
                if (length < 6 || at + length > frame.Length)
                    throw new PrintServerException("The IBM i's print server sent a code point that doesn't fit.", lost: true);
                points[BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(at + 4))] = frame.AsSpan(at + 6, length - 6).ToArray();
                at += length;
            }
            return new NpReply(BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(12)), (flags & 0x80000000) == 0, rc, points);
        }
    }
}

/// <summary>Network Print server attribute IDs (PrintObject in the Toolbox for Java).</summary>
public static class NpAttribute
{
    public const ushort Date = 0x0022;
    public const ushort FormType = 0x0030;
    public const ushort JobName = 0x003B;
    public const ushort JobNumber = 0x003C;
    public const ushort JobUser = 0x003E;
    public const ushort OutputQueueLibrary = 0x0049;
    public const ushort OutputQueue = 0x004A;
    public const ushort DeviceType = 0x005A;
    public const ushort SpooledFileName = 0x0068;
    public const ushort SpooledFileNumber = 0x0069;
    public const ushort Status = 0x006A;
    public const ushort Time = 0x006E;
    public const ushort Pages = 0x006F;
    public const ushort UserData = 0x0074;
    public const ushort NumberOfBytes = 0x007D;
    public const ushort MessageText = 0x0080;
    public const ushort ServerCcsid = 0x008A;
    public const ushort MessageId = 0x0093;
    public const ushort PrecomputeBytes = 0x00B8;
    public const ushort JobSystem = 0x00FB;

    /// <summary>What the list asks for about each spooled file.</summary>
    public static readonly ushort[] Listed =
    [
        Date, FormType, JobName, JobNumber, JobUser, OutputQueueLibrary, OutputQueue, DeviceType, SpooledFileName,
        SpooledFileNumber, Status, Time, Pages, UserData, JobSystem,
    ];
}

/// <summary>
/// Attribute code points (selection 0006, values 0008, spooled file ID 0001): a count (2), the
/// entry size (2, 12), the entries (ID 2, type 2, length 4, the value's offset from the start of
/// the code point including its 6-byte prefix 4), then the values. Numbers are 4-byte binary
/// (type 3); text is CCSID 37 ending in a zero byte (type 5). An ID list (0007) is a count, the
/// entry size (2) and the IDs.
/// </summary>
public static class NpAttributes
{
    private const ushort FourByte = 0x0003;
    private const ushort Text = 0x0005;
    private const int EntrySize = 12;

    public static byte[] Encode(IReadOnlyList<(ushort Id, object Value)> attributes)
    {
        var values = attributes.Select(a => a.Value switch
        {
            int i => (Type: FourByte, Bytes: BigEndian(i)),
            string s => (Type: Text, Bytes: Ebcdic.Encode(s + "\0")),
            _ => throw new ArgumentException($"attribute {a.Id:X4} must be a number or text"),
        }).ToList();
        var header = 4 + EntrySize * attributes.Count;
        var data = new byte[header + values.Sum(v => v.Bytes.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)attributes.Count);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), EntrySize);
        var valueAt = header;
        for (var i = 0; i < attributes.Count; i++)
        {
            var entry = data.AsSpan(4 + EntrySize * i);
            BinaryPrimitives.WriteUInt16BigEndian(entry, attributes[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(entry[2..], values[i].Type);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], values[i].Bytes.Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], valueAt + 6);
            values[i].Bytes.CopyTo(data, valueAt);
            valueAt += values[i].Bytes.Length;
        }
        return data;
    }

    public static byte[] EncodeIds(IReadOnlyList<ushort> ids)
    {
        var data = new byte[4 + 2 * ids.Count];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)ids.Count);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 2);
        for (var i = 0; i < ids.Count; i++) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4 + 2 * i), ids[i]);
        return data;
    }

    /// <summary>Numbers as int, text (trimmed) as string. Other types and entries that don't fit are left out.</summary>
    public static Dictionary<ushort, object> Decode(ReadOnlySpan<byte> data)
    {
        var attributes = new Dictionary<ushort, object>();
        if (data.Length < 4) return attributes;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data);
        var size = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (size < EntrySize) return attributes;
        for (var i = 0; i < count; i++)
        {
            var at = 4 + size * i;
            if (at + EntrySize > data.Length) break;
            var id = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            var length = BinaryPrimitives.ReadInt32BigEndian(data[(at + 4)..]);
            var offset = BinaryPrimitives.ReadInt32BigEndian(data[(at + 8)..]) - 6;
            if (length < 0 || offset < 0 || offset + length > data.Length) continue;
            var value = data.Slice(offset, length);
            switch (type)
            {
                case 0x0003 or 0x0004 when length >= 4:
                    attributes[id] = BinaryPrimitives.ReadInt32BigEndian(value);
                    break;
                case 0x0001 or 0x0002 when length >= 2:
                    attributes[id] = (int)BinaryPrimitives.ReadInt16BigEndian(value);
                    break;
                case 0x0005 or 0x0006 or 0x0009:
                {
                    var end = value.IndexOf((byte)0);
                    attributes[id] = Ebcdic.Decode(end >= 0 ? value[..end] : value).Trim();
                    break;
                }
            }
        }
        return attributes;
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
