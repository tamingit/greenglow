using System.Net;

namespace GreenGlow.FakeHost.Tests;

/// <summary>The fake IBM i's command line, as the tests and the demo container start it.</summary>
public class FakeHostArgsTests
{
    [Fact]
    public void ByDefaultItListensOnLoopbackPort2323WithoutAPrintServer() =>
        Assert.Equal(new FakeHostArgs(2323, null, IPAddress.Loopback), FakeHostArgs.Parse([]));

    [Fact]
    public void TheDemoContainerListensOnEveryInterface() =>
        Assert.Equal(new FakeHostArgs(2323, 8474, IPAddress.Any),
            FakeHostArgs.Parse(["--bind", "0.0.0.0", "--port", "2323", "--print-port", "8474"]));

    [Theory]
    [InlineData("--port", "x")]
    [InlineData("--print-port", "x")]
    [InlineData("--bind", "not-an-address")]
    public void ABadValueStopsTheStart(string option, string value) =>
        Assert.Throws<ArgumentException>(() => FakeHostArgs.Parse([option, value]));

    [Fact]
    public async Task ItCanListenOnAnotherAddress()
    {
        await using var host = await FakeIbmI.StartAsync(0, address: IPAddress.Any);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Port);
        Assert.True(client.Connected);
    }
}
