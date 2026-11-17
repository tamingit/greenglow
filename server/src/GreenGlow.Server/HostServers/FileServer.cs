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
/// One entry of a folder as the file server lists it. The size is the entry's own: for a
/// symbolic link that is the link, not what it points to (an open follows the link).
/// </summary>
public sealed record FolderEntry(string Name, bool Folder, long Size, DateTimeOffset Modified, int ObjectType, int Attributes);

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
/// reply 8002), read (3, reply 8003; no data at the end of the file), close (9, reply 8004) and
/// list (10: a chain of 8005 replies, one per entry, ended by return code 18; an empty folder is
/// return code 2 alone).
/// </summary>
public sealed class FileServerClient : IAsyncDisposable
{
    public const ushort ServerId = 0xE002;

    /// <summary>How much one read asks for.</summary>
    public const int ReadBlock = 64 * 1024;

    /// <summary>Longer paths are refused before anything is sent.</summary>
    public const int MaxPathLength = 4096;

    private const ushort ExchangeRequest = 22, OpenRequest = 2, ReadRequest = 3, WriteRequest = 4, CloseRequest = 9, ListRequest = 10,
        DeleteFileRequest = 12, CreateFolderRequest = 13, DeleteFolderRequest = 14, RenameRequest = 15;
    private const ushort ReturnCodeReply = 0x8001, OpenReply = 0x8002, ReadReply = 0x8003, CloseReply = 0x8004, ListReply = 0x8005,
        ExchangeReply = 0x8009, WriteReply = 0x800B;

    /// <summary>The return code for a name that is already there (a file or folder of that name).</summary>
    public const int AlreadyThere = 4;

    /// <summary>The return code for a folder (or a folder on the way) that isn't there.</summary>
    public const int NoSuchFolder = 3;

    private const ushort AccessWrite = 2;

    /// <summary>Open options for writing: create or replace what is there; create, but refuse when something is.</summary>
    private const ushort CreateOrReplace = 2, CreateNew = 4;

    /// <summary>The return code that ends a listing: no more entries.</summary>
    public const int NoMoreFiles = 18;

    /// <summary>The return code for a listing that matched nothing: the folder is there, and empty.</summary>
    public const int NothingMatched = 2;

    /// <summary>The fixed attribute of a folder (as in DOS: 0x10).</summary>
    private const int FolderAttribute = 0x10;

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

    /// <summary>The IBM i user signed on, upper case.</summary>
    public string User => _host.User;

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
            : reply.Length >= 66 ? BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(62)) : -1;
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

    /// <summary>
    /// The entries of a folder (without . and ..), as many as `most`: the server stops there.
    /// Throws FileServerException 3 when there is no such folder.
    /// </summary>
    public async Task<IReadOnlyList<FolderEntry>> ListAsync(string folder, int most, CancellationToken cancel)
    {
        var pattern = folder.TrimEnd('/') + "/*";
        await _host.SendAsync(List(_host.NextCorrelation(), PathBytes(pattern), (ushort)Ccsid, most), cancel);
        var entries = new List<FolderEntry>();
        while (true)
        {
            var reply = await _host.ReceiveAsync(cancel);
            if (reply.Length < 22 || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)) != ServerId) throw Malformed("list");
            var id = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(18));
            if (id == ReturnCodeReply)
            {
                var code = reply.Length >= 24 ? BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22)) : -1;
                // 18 ends a listing; 2 (no file matched the pattern) is how the IBM i answers for
                // an empty folder. 3, no such folder, is a refusal.
                if (code is 0 or NoMoreFiles or NothingMatched) return entries;
                throw new FileServerException(code, Describe(code));
            }
            if (id != ListReply) throw Malformed($"reply {id:X4}");
            if (Entry(reply, DatastreamLevel) is { } entry && entry.Name is not ("." or "..")) entries.Add(entry);
            if (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)) == 0) return entries;
        }
    }

    /// <summary>
    /// One list reply (8005): the modification date at 30 (seconds, then microseconds), the size
    /// at 81 (64-bit, from level 8; 32-bit at 46 before), the fixed attributes at 50, the object
    /// type at 54, and the name after the template (length, code point, UTF-16).
    /// </summary>
    public static FolderEntry? Entry(byte[] reply, int level)
    {
        var template = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(16));
        var at = 20 + template;
        if (reply.Length < Math.Max(at + 6, 89)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at));
        if (length < 6 || at + length > reply.Length || (length - 6) % 2 != 0) return null;
        var name = Encoding.BigEndianUnicode.GetString(reply, at + 6, length - 6);
        var size = level >= 8 && level != 0xF4F4
            ? BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(81))
            : BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(46));
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(30));
        var attributes = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(50));
        var type = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(54));
        var folder = type is 1 or 2 && (attributes & FolderAttribute) != 0;
        return new FolderEntry(name, folder, size, DateTimeOffset.FromUnixTimeSeconds(seconds), type, attributes);
    }

    /// <summary>
    /// Opens a file for writing, creating it, tagged with `fileCcsid`. With `replace`, a file of
    /// that name is emptied first; without, it is refused (FileServerException 4). A folder on the
    /// way that isn't there is refused (3).
    /// </summary>
    public async Task<int> OpenForWriteAsync(string path, bool replace, ushort fileCcsid, CancellationToken cancel)
    {
        await _host.SendAsync(Open(_host.NextCorrelation(), PathBytes(path), (ushort)Ccsid, DatastreamLevel, AccessWrite,
            replace ? CreateOrReplace : CreateNew, fileCcsid), cancel);
        var reply = await ReceiveAsync(_host, OpenReply, 26, cancel);
        return BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(22));
    }

    /// <summary>Writes bytes at `offset` (reply 800B: the return code at 22, the length not written at 28).</summary>
    public async Task WriteAsync(int handle, long offset, ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        await _host.SendAsync(Write(_host.NextCorrelation(), handle, offset, data.Span, DatastreamLevel), cancel);
        var reply = await ReceiveAsync(_host, WriteReply, 24, cancel);
        var code = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22));
        if (code != 0) throw new FileServerException(code, Describe(code));
        if (reply.Length >= 32 && BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(28)) is > 0 and var left)
            throw new FileServerException(11, $"The IBM i wrote all but {left} bytes. It may be out of space.");
    }

    /// <summary>
    /// Copies `source` into a new file (or, with `replace`, over one that is there), in blocks,
    /// closing it on the IBM i whatever happens. Returns the bytes written.
    /// </summary>
    public async Task<long> UploadAsync(string path, Stream source, bool replace, ushort fileCcsid, CancellationToken cancel)
    {
        var handle = await OpenForWriteAsync(path, replace, fileCcsid, cancel);
        var buffer = new byte[ReadBlock];
        long written = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancel);
                if (read == 0) break;
                await WriteAsync(handle, written, buffer.AsMemory(0, read), cancel);
                written += read;
                if (read < buffer.Length) break;
            }
        }
        catch
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await CloseAsync(handle, quick.Token); } catch { /* the connection is gone */ }
            throw;
        }
        await CloseAsync(handle, cancel);
        return written;
    }

    /// <summary>Creates a folder. Throws FileServerException 4 when something of that name is there, 3 when its folder isn't.</summary>
    public async Task CreateFolderAsync(string path, CancellationToken cancel)
    {
        await _host.SendAsync(CreateFolder(_host.NextCorrelation(), PathBytes(path), (ushort)Ccsid), cancel);
        await ReturnCodeAsync(cancel);
    }

    /// <summary>Makes sure a folder is there, creating it and any folders on the way that aren't.</summary>
    public async Task EnsureFolderAsync(string path, CancellationToken cancel)
    {
        path = path.TrimEnd('/');
        if (path.Length == 0) return;
        try
        {
            await CreateFolderAsync(path, cancel);
        }
        catch (FileServerException e) when (e.Code == AlreadyThere)
        {
        }
        catch (FileServerException e) when (e.Code == NoSuchFolder)
        {
            await EnsureFolderAsync(path[..path.LastIndexOf('/')], cancel);
            await CreateFolderAsync(path, cancel);
        }
    }

    public async Task DeleteFileAsync(string path, CancellationToken cancel)
    {
        await _host.SendAsync(DeleteFile(_host.NextCorrelation(), PathBytes(path), (ushort)Ccsid), cancel);
        await ReturnCodeAsync(cancel);
    }

    /// <summary>Deletes an empty folder (FileServerException 9 when it isn't empty).</summary>
    public async Task DeleteFolderAsync(string path, CancellationToken cancel)
    {
        await _host.SendAsync(DeleteFolder(_host.NextCorrelation(), PathBytes(path), (ushort)Ccsid), cancel);
        await ReturnCodeAsync(cancel);
    }

    /// <summary>
    /// Renames (or moves) a file or folder. A name already in use is refused (FileServerException
    /// 4): nothing is replaced.
    /// </summary>
    public async Task RenameAsync(string path, string newPath, CancellationToken cancel)
    {
        await _host.SendAsync(Rename(_host.NextCorrelation(), PathBytes(path), PathBytes(newPath), (ushort)Ccsid), cancel);
        await ReturnCodeAsync(cancel);
    }

    /// <summary>
    /// Deletes a folder and everything in it: each file (or link) first, then each folder, from
    /// the deepest up. Stops at the first refusal, which is thrown. Returns how many entries went.
    /// </summary>
    public async Task<int> DeleteTreeAsync(string folder, CancellationToken cancel)
    {
        var deleted = 0;
        foreach (var entry in await ListAsync(folder, 0, cancel))
        {
            var path = folder.TrimEnd('/') + "/" + entry.Name;
            if (entry.Folder) deleted += await DeleteTreeAsync(path, cancel);
            else
            {
                await DeleteFileAsync(path, cancel);
                deleted++;
            }
        }
        await DeleteFolderAsync(folder, cancel);
        return deleted + 1;
    }

    /// <summary>A request answered by a bare return code (8001): 0 is done, anything else is thrown.</summary>
    private async Task ReturnCodeAsync(CancellationToken cancel)
    {
        var reply = await _host.ReceiveAsync(cancel);
        if (reply.Length < 24 || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)) != ServerId
            || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(18)) != ReturnCodeReply)
            throw Malformed("reply");
        var code = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22));
        if (code != 0) throw new FileServerException(code, Describe(code));
    }

    public async Task CloseAsync(int handle, CancellationToken cancel)
    {
        await _host.SendAsync(Close(_host.NextCorrelation(), handle), cancel);
        await ReceiveAsync(_host, CloseReply, 22, cancel);
    }

    /// <summary>Copies a whole file into `destination`, closing it on the IBM i whatever happens. Returns the bytes copied.</summary>
    public async Task<long> DownloadAsync(string path, Stream destination, CancellationToken cancel) =>
        await CopyToAsync(await OpenForReadAsync(path, cancel), destination, limit: null, cancel);

    /// <summary>
    /// Copies an opened file into `destination` until its end, or until `limit` bytes (the size
    /// the open reported, when the copy promised that many), then closes it on the IBM i, whatever
    /// happens. A failed write closes the file too, so the connection stays in step with its replies.
    /// </summary>
    public async Task<long> CopyToAsync(OpenedFile file, Stream destination, long? limit, CancellationToken cancel)
    {
        long copied = 0;
        while (limit is null || copied < limit)
        {
            byte[] block;
            try
            {
                var ask = limit is { } most ? (int)Math.Min(ReadBlock, most - copied) : ReadBlock;
                block = await ReadAsync(file.Handle, copied, ask, cancel);
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
        4 => "Something of that name is already there.",
        9 => "The folder isn't empty.",
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

    /// <summary>
    /// Open (2): template 44 (36 below level 16); the name's CCSID at 22, the file data's CCSID at
    /// 28 (for a file it creates), access at 30 (1 read, 2 write), share at 32 (0: anyone), the open
    /// option at 36 (1 open or create, 2 create or replace, 4 create but fail if there, 8 open but
    /// fail if not); the name's length at 64 (56), code point 2, the name at 70 (62).
    /// </summary>
    public static byte[] Open(int correlation, byte[] path, ushort ccsid, int level, ushort access, ushort option, ushort fileCcsid = 0)
    {
        var template = (ushort)(level >= 16 ? 44 : 36);
        var frame = NewFrame(20 + template + 6 + path.Length, correlation, template, OpenRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], ccsid);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], fileCcsid);
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

    /// <summary>
    /// List attributes (10) by name: template 20; handle 0 at 22, the CCSID at 26, 1 at 28, the
    /// most entries at 34 (0xFFFF: no limit), attribute level 1 at 36, pattern matching 0 at 38;
    /// the pattern (a folder, then /*) as a length at 40, code point 2 at 44, the name from 46.
    /// </summary>
    public static byte[] List(int correlation, byte[] pattern, ushort ccsid, int most)
    {
        var frame = NewFrame(46 + pattern.Length, correlation, 20, ListRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], ccsid);
        BinaryPrimitives.WriteInt32BigEndian(span[28..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[34..], most is > 0 and < 0xFFFF ? (ushort)most : (ushort)0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(span[36..], 1);
        BinaryPrimitives.WriteInt32BigEndian(span[40..], pattern.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[44..], 2);
        pattern.CopyTo(frame, 46);
        return frame;
    }

    /// <summary>
    /// Write (4): template 34 (18 below level 16); handle at 22, offset 64-bit at 46 (32-bit at
    /// 30), data flags 2 at 34, the data's CCSID at 36 (65535: bytes as they are); the data as
    /// code point 0020 with its length at 54 (38), the bytes from 60 (44).
    /// </summary>
    public static byte[] Write(int correlation, int handle, long offset, ReadOnlySpan<byte> data, int level)
    {
        var template = (ushort)(level >= 16 ? 34 : 18);
        var ll = 20 + template;
        var frame = NewFrame(ll + 6 + data.Length, correlation, template, WriteRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[22..], handle);
        if (level >= 16) BinaryPrimitives.WriteInt64BigEndian(span[46..], offset);
        else BinaryPrimitives.WriteInt32BigEndian(span[30..], checked((int)offset));
        BinaryPrimitives.WriteUInt16BigEndian(span[34..], 2);
        BinaryPrimitives.WriteUInt16BigEndian(span[36..], 0xFFFF);
        BinaryPrimitives.WriteInt32BigEndian(span[ll..], data.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[(ll + 4)..], 0x0020);
        data.CopyTo(span[(ll + 6)..]);
        return frame;
    }

    /// <summary>Create folder (13) and delete file (12): template 8; CCSID at 22, 1 at 24, the name's length at 28, its code point (1 a folder, 2 a file) at 32, the name from 34.</summary>
    public static byte[] ByName(ushort request, ushort codePoint, int correlation, byte[] path, ushort ccsid)
    {
        var frame = NewFrame(34 + path.Length, correlation, 8, request);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], ccsid);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], 1);
        BinaryPrimitives.WriteInt32BigEndian(span[28..], path.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[32..], codePoint);
        path.CopyTo(frame, 34);
        return frame;
    }

    public static byte[] CreateFolder(int correlation, byte[] path, ushort ccsid) => ByName(CreateFolderRequest, 1, correlation, path, ccsid);

    public static byte[] DeleteFile(int correlation, byte[] path, ushort ccsid) => ByName(DeleteFileRequest, 2, correlation, path, ccsid);

    /// <summary>Delete folder (14): template 10; CCSID at 22, 1 at 24, flags 0 at 28, the name's length at 30, code point 1 at 34, the name from 36.</summary>
    public static byte[] DeleteFolder(int correlation, byte[] path, ushort ccsid)
    {
        var frame = NewFrame(36 + path.Length, correlation, 10, DeleteFolderRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], ccsid);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], 1);
        BinaryPrimitives.WriteInt32BigEndian(span[30..], path.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[34..], 1);
        path.CopyTo(frame, 36);
        return frame;
    }

    /// <summary>
    /// Rename (15): template 16; the two names' CCSIDs at 22 and 24, 1 at 26 and at 30, replace 0
    /// at 34 (never replace); the old name as a length at 36, code point 3 at 40, the name from 42;
    /// then the new name as a length, code point 4 and the name.
    /// </summary>
    public static byte[] Rename(int correlation, byte[] path, byte[] newPath, ushort ccsid)
    {
        var frame = NewFrame(42 + path.Length + 6 + newPath.Length, correlation, 16, RenameRequest);
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], ccsid);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], ccsid);
        BinaryPrimitives.WriteInt32BigEndian(span[26..], 1);
        BinaryPrimitives.WriteInt32BigEndian(span[30..], 1);
        BinaryPrimitives.WriteInt32BigEndian(span[36..], path.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[40..], 3);
        path.CopyTo(frame, 42);
        var at = 42 + path.Length;
        BinaryPrimitives.WriteInt32BigEndian(span[at..], newPath.Length + 6);
        BinaryPrimitives.WriteUInt16BigEndian(span[(at + 4)..], 4);
        newPath.CopyTo(frame, at + 6);
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
