using System.Net;
using GreenGlow.FakeHost;

namespace GreenGlow.Server.Tests;

/// <summary>Each connection's maxSessionsPerUser, from the seed, caps one user's host sessions on it.</summary>
public sealed class SessionLimitPerUserTests
{
    private static async Task<(Rig Rig, FakeIbmI Host, RelayFactory Factory)> StartAsync(int maxSessionsPerUser)
    {
        var host = await FakeIbmI.StartAsync();
        var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson($"test=tcp://127.0.0.1:{host.Port}", maxSessionsPerUser: maxSessionsPerUser),
        });
        return (Rig.Wrap(host, factory), host, factory);
    }

    [Fact]
    public async Task OneSessionPerUserRefusesTheSecondButNotAnotherUsers()
    {
        var (rig, host, factory) = await StartAsync(1);
        await using var _ = host;
        using var __ = factory;
        var first = await rig.OpenOkAsync();
        Assert.EndsWith("1", first.Device);

        var second = await rig.OpenAsync();
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("limit", second.Error);
        Assert.Contains("1 session on", second.Body.GetProperty("message").GetString());

        var other = await rig.OpenAsync(upn: "someone-else@example.com");
        Assert.Equal(HttpStatusCode.Created, other.Status);

        // Ending the first frees the place.
        await rig.DeleteAsync(first.Id);
        await Wait.UntilAsync(async () => (await rig.OpenAsync()).Status == HttpStatusCode.Created, "a place to free up");
    }

    [Fact]
    public async Task FiveSessionsPerUserUseFiveDeviceNames()
    {
        var (rig, host, factory) = await StartAsync(5);
        await using var _ = host;
        using var __ = factory;
        var devices = new List<string?>();
        for (var i = 0; i < 5; i++) devices.Add((await rig.OpenOkAsync()).Device);
        Assert.Equal(Enumerable.Range(1, 5).Select(n => $"GG1{n:D3}"), devices);
        Assert.Equal("limit", (await rig.OpenAsync()).Error);
    }

    [Fact]
    public async Task TwoConnectionsToOneIbmIDontShareADeviceName()
    {
        // Two connections to the same fake IBM i, as TLS and Telnet to one system would be.
        var host = await FakeIbmI.StartAsync();
        await using var _ = host;
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson(
                $"test=tcp://127.0.0.1:{host.Port},again=tcp://127.0.0.1:{host.Port},other=tcp://localhost:{host.Port}",
                maxSessionsPerUser: 2),
        });
        var rig = Rig.Wrap(host, factory);
        Assert.Equal("GG1001", (await rig.OpenOkAsync("test")).Device);
        Assert.Equal("GG1002", (await rig.OpenOkAsync("again")).Device);
        Assert.Equal("GG1003", (await rig.OpenOkAsync("again")).Device);
        // Each connection keeps its own limit.
        Assert.Equal("limit", (await rig.OpenAsync("again")).Error);
        Assert.Equal("GG1004", (await rig.OpenOkAsync("test")).Device);
        // Another host name is taken as another system, even if it is the same one underneath.
        Assert.Equal("GG1001", (await rig.OpenOkAsync("other")).Device);
    }
}
