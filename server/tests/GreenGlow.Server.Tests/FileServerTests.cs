using System.Buffers.Binary;
using GreenGlow.Server.HostServers;

namespace GreenGlow.Server.Tests;

/// <summary>
/// The file server's requests, byte for byte, as verified against a real IBM i at datastream
/// level 24 (FileServerLiveTests), and below level 16 as IBM's client lays them out.
/// </summary>
public sealed class FileServerTests
{
    private static int Int(byte[] b, int at) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at));
    private static ushort Short(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private static void Header(byte[] frame, int correlation, ushort template, ushort request)
    {
        Assert.Equal(frame.Length, Int(frame, 0));
        Assert.Equal(0, Short(frame, 4));
        Assert.Equal(FileServerClient.ServerId, Short(frame, 6));
        Assert.Equal(correlation, Int(frame, 12));
        Assert.Equal(template, Short(frame, 16));
        Assert.Equal(request, Short(frame, 18));
    }

    [Fact]
    public void ExchangeOffersTheCcsids()
    {
        var frame = FileServerClient.Exchange(1, 0x60100, [13488, 0xF200]);
        Header(frame, 1, 10, 22);
        Assert.Equal(40, frame.Length);
        Assert.Equal(0x0100, Short(frame, 22));
        Assert.Equal(-1, Int(frame, 26));
        Assert.Equal(10, Int(frame, 30));
        Assert.Equal(10, Short(frame, 34));
        Assert.Equal(13488, Short(frame, 36));
        Assert.Equal(0xF200, Short(frame, 38));
    }

    [Theory]
    [InlineData(24, 44, 64)]
    [InlineData(8, 36, 56)]
    public void OpenPutsTheNameAfterTheTemplate(int level, ushort template, int nameLength)
    {
        var path = FileServerClient.PathBytes("/a/b.txt");
        var frame = FileServerClient.Open(7, path, 1200, level, 1, 8);
        Header(frame, 7, template, 2);
        Assert.Equal(20 + template + 6 + path.Length, frame.Length);
        Assert.Equal(1200, Short(frame, 22));
        Assert.Equal(1, Int(frame, 24));
        Assert.Equal(1, Short(frame, 30));
        Assert.Equal(8, Short(frame, 36));
        Assert.Equal(1, Short(frame, 46));
        Assert.Equal(path.Length + 6, Int(frame, nameLength));
        Assert.Equal(2, Short(frame, nameLength + 4));
        Assert.Equal(path, frame[(nameLength + 6)..]);
    }

    [Fact]
    public void ReadUsesA64BitOffsetFromLevel16()
    {
        var frame = FileServerClient.Read(3, 9, 0x1_0000_0000, 65536, 24);
        Header(frame, 3, 38, 3);
        Assert.Equal(58, frame.Length);
        Assert.Equal(9, Int(frame, 22));
        Assert.Equal(0x1_0000_0000, BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(50)));
        Assert.Equal(65536, Int(frame, 34));

        var old = FileServerClient.Read(4, 9, 512, 100, 8);
        Header(old, 4, 22, 3);
        Assert.Equal(512, Int(old, 30));
        Assert.Throws<OverflowException>(() => FileServerClient.Read(5, 9, 0x1_0000_0000, 100, 8));
    }

    [Fact]
    public void CloseIsFixed()
    {
        var frame = FileServerClient.Close(5, 9);
        Header(frame, 5, 21, 9);
        Assert.Equal(41, frame.Length);
        Assert.Equal(9, Int(frame, 22));
        Assert.Equal(2, Short(frame, 26));
        Assert.Equal(0xFFFF, Short(frame, 28));
        Assert.Equal(100, Short(frame, 30));
    }

    [Fact]
    public void ListAsksForTheFolderPattern()
    {
        var pattern = FileServerClient.PathBytes("/home/me/*");
        var frame = FileServerClient.List(6, pattern, 1200, 500);
        Header(frame, 6, 20, 10);
        Assert.Equal(46 + pattern.Length, frame.Length);
        Assert.Equal(0, Int(frame, 22));
        Assert.Equal(1200, Short(frame, 26));
        Assert.Equal(1, Int(frame, 28));
        Assert.Equal(500, Short(frame, 34));
        Assert.Equal(1, Short(frame, 36));
        Assert.Equal(0, Short(frame, 38));
        Assert.Equal(pattern.Length + 6, Int(frame, 40));
        Assert.Equal(2, Short(frame, 44));
        Assert.Equal(pattern, frame[46..]);
        Assert.Equal(0xFFFF, Short(FileServerClient.List(7, pattern, 1200, 0), 34));
    }

    [Theory]
    [InlineData(2, 0x10, "docs", true, 8192)]
    [InlineData(1, 0x20, "report.csv", false, 0x1_2345_6789)]
    [InlineData(3, 0x10, "a-link", false, 74)]
    public void AListReplyIsAnEntry(int type, int attributes, string name, bool folder, long size)
    {
        // As the IBM i sends one (level 24): a 93-byte template, then the name.
        var nameBytes = FileServerClient.PathBytes("/" + name)[2..];
        var reply = new byte[20 + 93 + 6 + nameBytes.Length];
        var s = reply.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(s, reply.Length);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], FileServerClient.ServerId);
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], 93);
        BinaryPrimitives.WriteUInt16BigEndian(s[18..], 0x8005);
        BinaryPrimitives.WriteUInt16BigEndian(s[20..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(s[30..], 1_700_000_000);
        BinaryPrimitives.WriteInt32BigEndian(s[50..], attributes);
        BinaryPrimitives.WriteUInt16BigEndian(s[54..], (ushort)type);
        BinaryPrimitives.WriteInt64BigEndian(s[81..], size);
        BinaryPrimitives.WriteInt32BigEndian(s[113..], 6 + nameBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(s[117..], 2);
        nameBytes.CopyTo(reply, 119);

        var entry = FileServerClient.Entry(reply, 24)!;
        Assert.Equal((name, folder, size, type), (entry.Name, entry.Folder, entry.Size, entry.ObjectType));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), entry.Modified);
        Assert.Null(FileServerClient.Entry(reply[..100], 24));
    }

    [Fact]
    public void OpenForWritingTagsTheFileAndSaysWhatToDoWhenItIsThere()
    {
        var path = FileServerClient.PathBytes("/tmp/a.csv");
        var frame = FileServerClient.Open(8, path, 1200, 24, 2, 4, 1208);
        Assert.Equal(1200, Short(frame, 22));
        Assert.Equal(1208, Short(frame, 28));
        Assert.Equal(2, Short(frame, 30));
        Assert.Equal(0, Short(frame, 32));
        Assert.Equal(4, Short(frame, 36));
    }

    [Fact]
    public void WriteCarriesTheBytesAsCodePoint0020()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var frame = FileServerClient.Write(9, 7, 0x1_0000_0000, data, 24);
        Header(frame, 9, 34, 4);
        Assert.Equal(60 + data.Length, frame.Length);
        Assert.Equal(7, Int(frame, 22));
        Assert.Equal(0x1_0000_0000, BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(46)));
        Assert.Equal(2, Short(frame, 34));
        Assert.Equal(0xFFFF, Short(frame, 36));
        Assert.Equal(data.Length + 6, Int(frame, 54));
        Assert.Equal(0x0020, Short(frame, 58));
        Assert.Equal(data, frame[60..]);

        var old = FileServerClient.Write(10, 7, 512, data, 8);
        Header(old, 10, 18, 4);
        Assert.Equal(512, Int(old, 30));
        Assert.Equal(data, old[44..]);
    }

    [Theory]
    [InlineData(13, 1)]
    [InlineData(12, 2)]
    public void CreateFolderAndDeleteFileNameThePath(ushort request, ushort codePoint)
    {
        var path = FileServerClient.PathBytes("/tmp/x");
        var frame = request == 13 ? FileServerClient.CreateFolder(11, path, 1200) : FileServerClient.DeleteFile(11, path, 1200);
        Header(frame, 11, 8, request);
        Assert.Equal(34 + path.Length, frame.Length);
        Assert.Equal(1200, Short(frame, 22));
        Assert.Equal(1, Int(frame, 24));
        Assert.Equal(path.Length + 6, Int(frame, 28));
        Assert.Equal(codePoint, Short(frame, 32));
        Assert.Equal(path, frame[34..]);
    }

    [Fact]
    public void DeleteFolderHasItsFlagsBeforeTheName()
    {
        var path = FileServerClient.PathBytes("/tmp/x");
        var frame = FileServerClient.DeleteFolder(12, path, 1200);
        Header(frame, 12, 10, 14);
        Assert.Equal(36 + path.Length, frame.Length);
        Assert.Equal(0, Short(frame, 28));
        Assert.Equal(path.Length + 6, Int(frame, 30));
        Assert.Equal(1, Short(frame, 34));
        Assert.Equal(path, frame[36..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/nul\0inside")]
    public void APathMustBeAbsolute(string path) =>
        Assert.Throws<ArgumentException>(() => FileServerClient.PathBytes(path));

    [Fact]
    public void PathsAreUtf16BigEndian() =>
        Assert.Equal([0x00, (byte)'/', 0x00, (byte)'a', 0x00, 0xE9], FileServerClient.PathBytes("/aé"));
}
