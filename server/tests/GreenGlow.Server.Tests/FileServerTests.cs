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
