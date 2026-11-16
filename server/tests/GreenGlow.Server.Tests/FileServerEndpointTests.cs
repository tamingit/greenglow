using GreenGlow.FakeHost;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.HostServers;

namespace GreenGlow.Server.Tests;

/// <summary>
/// The file server's port: the port mapper's answer when it has one, IBM's default (8473, or 9473
/// over TLS) when it can't say.
/// </summary>
public sealed class FileServerEndpointTests
{
    private static Target Telnet(string url) => TargetParser.ParseUrl("system", "System", url);

    [Fact]
    public async Task TheFileServerPortComesFromThePortMapper()
    {
        await using var mapper = new FakePortMapper().Map("as-file", 18473);
        var endpoints = new HostEndpoints(new HostServerOptions { MapperPort = mapper.Port }, new PrinterOptions(), TimeProvider.System);
        var target = await endpoints.ForFileServerAsync(Telnet("tcp://127.0.0.1:23"), T.Token());
        Assert.Equal(18473, target.Port);
        Assert.Equal(1, mapper.Lookups);
    }

    [Fact]
    public async Task TheDefaultPortIsUsedWhenThePortMapperDoesNotKnowTheService()
    {
        await using var mapper = new FakePortMapper().Map("as-signon", 18476);
        var endpoints = new HostEndpoints(new HostServerOptions { MapperPort = mapper.Port }, new PrinterOptions(), TimeProvider.System);
        var target = await endpoints.ForFileServerAsync(Telnet("tcp://127.0.0.1:23"), T.Token());
        Assert.Equal(HostService.FileServer.Port, target.Port);
    }

    [Fact]
    public async Task TheDefaultPortIsUsedWhenThePortMapperIsDown()
    {
        // Nothing listens on the mapper's port, so the lookup fails and the defaults apply.
        var endpoints = new HostEndpoints(new HostServerOptions { MapperPort = 1 }, new PrinterOptions(), TimeProvider.System);
        Assert.Equal(8473, (await endpoints.ForFileServerAsync(Telnet("tcp://127.0.0.1:23"), T.Token())).Port);
        Assert.Equal(9473, (await endpoints.ForFileServerAsync(Telnet("tls://127.0.0.1:992"), T.Token())).Port);
    }
}
