using System.Buffers.Binary;
using System.Text;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.HostServers;

/// <summary>A file server request the IBM i refused, with its return code.</summary>
public sealed class FileServerException(int code, string message) : Exception(message)
{
    /// <summary>The file server's return code (2 file not found, 3 path not found, 13 access denied ...).</summary>
    public int Code { get; } = code;
}

/// <summary>A file opened for reading: its handle on this connection and its size in bytes.</summary>
public sealed record OpenedFile(int Handle, long Size);

/// <summary>
/// The IBM i file server (as-file, server ID E002, port 8473 or 9473 over TLS, found through the
/// port mapper first): the integrated file system by path. It signs on as every host server
/// does, then exchanges attributes, which settles the datastream level and the CCSID of path
/// names (UTF-16 or UCS-2, big-endian). After the 20-byte header each request has a fixed
/// template whose length and layout depend on that level (16 and up use 64-bit offsets and
/// sizes); a path follows the template as a length (4, counting itself), a code point (2) and
/// the name. A refusal is reply 8001 with the return code at byte 22.
///
/// Verified against a real IBM i (datastream level 24): exchange (22, reply 8009), open (2,
/// reply 8002), read (3, reply 8003; no data at the end of the file) and close (9, reply 8004).
/// </summary>
public sealed class FileServerClient : IAsyncDisposable
{
    public const ushort ServerId = 0xE002;

    /// <summary>How much one read asks for.</summary>
    public const int ReadBlock = 64 * 1024;

    /// <summary>Longer paths are refused before anything is sent.</summary>
    public const int MaxPathLength = 4096;

    private const ushort ExchangeRequest = 22, OpenRequest = 2, ReadRequest = 3, CloseRequest = 9;
    private const ushort ReturnCodeReply = 0x8001, OpenReply = 0x8002, ReadReply = 0x8003, CloseReply = 0x8004, ExchangeReply = 0x8009;

    /// <summary>The level asked for, as IBM's client asks for it on 6.1 and later; the server answers with the one it uses.</summary>
    private const int RequestedLevel = 0x60100;

    /// <summary>The CCSIDs offered for path names: UCS-2 (13488) and the flag IBM's client sends after it.</summary>
    private static readonly int[] OfferedCcsids = [13488, 0xF200];

    private const ushort AccessRead = 1;

    /// <summary>The open option IBM's client sends to open an existing file (fail if it isn't there).</summary>
    private const ushort OpenExisting = 8;

    private readonly HostServerClient _host;

    private FileServerClient(HostServerClient host, int level, int ccsid)
    {
        _host = host;
        DatastreamLevel = level;
        Ccsid = ccsid;
    }

    public int DatastreamLevel { get; }

    /// <summary>The CCSID the server takes path names in (1200 or 13488, both UTF-16 big-endian here).</summary>
    public int Ccsid { get; }

    /// <summary>Connects to the file server of the Telnet connection's IBM i, signs on and exchanges attributes.</summary>
    public static async Task<FileServerClient> ConnectAsync(HostConnector connector, HostEndpoints endpoints, Target telnet,
        string user, string password, CancellationToken cancel)
    {
        var endpoint = await endpoints.ForFileServerAsync(telnet, cancel);
        var host = await HostServerClient.SignOnAsync(connector, endpoint, HostService.FileServer, user, password, cancel);
        try
        {
            await host.SendAsync(Exchange(host.NextCorrelation(), RequestedLevel, OfferedCcsids), cancel);
            var reply = await ReceiveAsync(host, ExchangeReply, 38, cancel);
            var level = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22));
            var ccsid = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(36));
            return new FileServerClient(host, level, ccsid);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>Opens an existing file for reading. Throws FileServerException when the IBM i refuses.</summary>
    public async Task<OpenedFile> OpenForReadAsync(string path, CancellationToken cancel)
    {
        await _host.SendAsync(Open(_host.NextCorrelation(), PathBytes(path), (ushort)Ccsid, DatastreamLevel, AccessRead, OpenExisting), cancel);
        var reply = await ReceiveAsync(_host, OpenReply, 26, cancel);
        var handle = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(22));
        var size = DatastreamLevel >= 16
            ? reply.Length >= 97 ? BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(89)) : -1
            : reply.Length >= 52 ? BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(48)) : -1;
        return new OpenedFile(handle, size);
    }

    /// <summary>Reads up to `length` bytes at `offset`; an empty result is the end of the file.</summary>
    public async Task<byte[]> ReadAsync(int handle, long offset, int length, CancellationToken cancel)
    {
        await _host.SendAsync(Read(_host.NextCorrelation(), handle, offset, length, DatastreamLevel), cancel);
        var data = new MemoryStream();
        while (true)
        {
            var reply = await ReceiveAsync(_host, ReadReply, 30, cancel);
            var ll = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(24));
            if (ll < 6 || 24 + ll > reply.Length) throw Malformed("read");
            data.Write(reply, 30, ll - 6);
            if (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)) == 0) return data.ToArray();
        }
    }

    public async Task CloseAsync(int handle, CancellationToken cancel)
    {
        await _host.SendAsync(Close(_host.NextCorrelation(), handle), cancel);
        await ReceiveAsync(_host, CloseReply, 22, cancel);
    }

    /// <summary>Copies a whole file into `destination`, closing it on the IBM i whatever happens. Returns the bytes copied.</summary>
    public async Task<long> DownloadAsync(string path, Stream destination, CancellationToken cancel)
    {
        var file = await OpenForReadAsync(path, cancel);
        long copied = 0;
        while (true)
        {
            byte[] block;
            try
            {
                block = await ReadAsync(file.Handle, copied, ReadBlock, cancel);
                if (block.Length > 0) await destination.WriteAsync(block, cancel);
            }
            catch
            {
                // Close if the connection still works; the first error is the one that matters.
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await CloseAsync(file.Handle, quick.Token); } catch { /* the connection is gone */ }
                throw;
            }
            if (block.Length == 0) break;
            copied += block.Length;
        }
        await CloseAsync(file.Handle, cancel);
        return copied;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>A path as the file server takes it: absolute, UTF-16 big-endian.</summary>
    public static byte[] PathBytes(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('\0') || path.Length > MaxPathLength)
            throw new ArgumentException("An IFS path starts with / and has no NUL characters.", nameof(path));
        return Encoding.BigEndianUnicode.GetBytes(path);
    }

    /// <summary>What a file server return code means, for the person who asked.</summary>
    public static string Describe(int code) => code switch
    {
        1 => "The file is in use.",
        2 => "There is no such file.",
        3 => "There is no such folder.",
        4 => "That is a folder, not a file.",
        5 or 13 => "You aren't authorised to that file or folder on the IBM i.",
        7 => "That isn't a valid file name.",
        11 or 12 => "The IBM i hasn't the resources for that right now.",
        32 or 33 => "The file is locked by another job.",
        _ => $"The IBM i's file server refused the request (return code {code}).",
    };

    private static async Task<byte[]> ReceiveAsync(HostServerClient host, ushort expected, int minimum, CancellationToken cancel)
    {
        var reply = await host.ReceiveAsync(cancel);
        if (reply.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)) != ServerId) throw Malformed("reply");
        var id = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(18));
        if (id == ReturnCodeReply && reply.Length >= 24)
        {
            var code = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22));
            if (code != 0) throw new FileServerException(code, Describe(code));
            // Success as a bare return code is only a fit answer to close.
            return expected == CloseReply ? reply : throw Malformed($"reply {id:X4}");
        }
        if (id != expected || reply.Length < minimum) throw Malformed($"reply {id:X4}");
        return reply;
    }

    private static FileServerException Malformed(string what) =>
        new(-1, $"The IBM i's file server sent an unexpected {what}.");

    // ---- requests: header (20), then the template; offsets are from the start of the frame ----

    private static byte[] NewFrame(int length, int correlation, ushort template, ushort request)
    {
        var frame = new byte[length];
        var span = frame.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span, length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], ServerId);
        BinaryPrimitives.WriteInt32BigEndian(span[12..], correlation);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..], template);
        BinaryPrimitives.WriteUInt16BigEndian(span[18..], request);
        return frame;
    }

    /// <summary>Exchange attributes (22): template 10; the CCSID list as a length at 30, code point 10 at 34, two bytes each from 36.</summary>
    public static byte[] Exchange(int correlation, int level, IReadOnlyList<int> ccsids)
    {
        var frame = NewFrame(36 + 2 * ccsids.Count, correlation, 10, ExchangeRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], (ushort)(level & 0xFFFF));
        BinaryPrimitives.WriteInt32BigEndian(span[26..], -1);
        BinaryPrimitives.WriteInt32BigEndian(span[30..], 6 + 2 * ccsids.Count);
        BinaryPrimitives.WriteUInt16BigEndian(span[34..], 10);
        for (var i = 0; i < ccsids.Count; i++) BinaryPrimitives.WriteUInt16BigEndian(span[(36 + 2 * i)..], (ushort)ccsids[i]);
        return frame;
    }

    /// <summary>Open (2): template 44 (36 below level 16); the name's length at 64 (56), code point 2, the name at 70 (62).</summary>
    public static byte[] Open(int correlation, byte[] path, ushort ccsid, int level, ushort access, ushort option)
    {
        var template = (ushort)(level >= 16 ? 44 : 36);
        var frame = NewFrame(20 + template + 6 + path.Length, correlation, template, OpenRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], ccsid);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[30..], access);
        BinaryPrimitives.WriteUInt16BigEndian(span[36..], option);
        BinaryPrimitives.WriteUInt16BigEndian(span[46..], 1);
        var ll = 20 + template;
        BinaryPrimitives.WriteInt32BigEndian(span[ll..], path.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[(ll + 4)..], 2);
        path.CopyTo(frame, ll + 6);
        return frame;
    }

    /// <summary>Read (3): template 38 (22 below level 16); handle at 22, offset 64-bit at 50 (32-bit at 30), length at 34.</summary>
    public static byte[] Read(int correlation, int handle, long offset, int length, int level)
    {
        var template = (ushort)(level >= 16 ? 38 : 22);
        var frame = NewFrame(20 + template, correlation, template, ReadRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[22..], handle);
        if (level >= 16) BinaryPrimitives.WriteInt64BigEndian(span[50..], offset);
        else BinaryPrimitives.WriteInt32BigEndian(span[30..], checked((int)offset));
        BinaryPrimitives.WriteInt32BigEndian(span[34..], length);
        return frame;
    }

    /// <summary>Close (9): a fixed 41 bytes; handle at 22, data flags 2 at 26, CCSID 0xFFFF at 28, 100 at 30.</summary>
    public static byte[] Close(int correlation, int handle)
    {
        var frame = NewFrame(41, correlation, 21, CloseRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[22..], handle);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], 2);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(span[30..], 100);
        return frame;
    }
}
