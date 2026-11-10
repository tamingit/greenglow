using System.Net;
using GreenGlow.FakeHost;
using GreenGlow.Server.Cluster;
using GreenGlow.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

/// <summary>
/// Device names are GG, the node's number and the lowest free session number on that IBM i,
/// and a name is kept for its user for a reconnect.
/// </summary>
public sealed class DeviceNameTests
{
    private static async Task<(Rig Rig, FakeIbmI Host, RelayFactory Factory, FakeTimeProvider Clock)> StartAsync(
        int node = 1, Func<int, string>? targets = null, int maxSessionsPerUser = 3)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = await FakeIbmI.StartAsync();
        var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson(targets?.Invoke(host.Port) ?? $"test=tcp://127.0.0.1:{host.Port}",
                maxSessionsPerUser: maxSessionsPerUser),
            ["Cluster:NodeId"] = node.ToString(),
        })
        {
            ExtraServices = s => s.AddSingleton<TimeProvider>(clock),
        };
        return (Rig.Wrap(host, factory), host, factory, clock);
    }

    [Fact]
    public async Task NamesAreGhTheNodeAndTheLowestFreeNumberAndAFourthSessionIsRefused()
    {
        var (rig, host, factory, _) = await StartAsync();
        await using var _host = host;
        using var _factory = factory;
        Assert.Equal("GG1001", (await rig.OpenOkAsync()).Device);
        Assert.Equal("GG1002", (await rig.OpenOkAsync()).Device);
        Assert.Equal("GG1003", (await rig.OpenOkAsync()).Device);
        var fourth = await rig.OpenAsync();
        Assert.Equal(HttpStatusCode.Conflict, fourth.Status);
        Assert.Equal("limit", fourth.Error);
        // The fake clock gives every session the same open time, so the list is sorted here.
        Assert.Equal(["GG1001", "GG1002", "GG1003"], (await rig.ListAsync()).Sessions.Select(s => s.GetProperty("device").GetString()).Order());
    }

    [Fact]
    public async Task TheNodesNumberIsInItsNames()
    {
        var (rig, host, factory, _) = await StartAsync(node: 4);
        await using var _host = host;
        using var _factory = factory;
        Assert.Equal("GG4001", (await rig.OpenOkAsync()).Device);
    }

    [Fact]
    public async Task EveryUserSharesTheNumbersOfOneIbmI()
    {
        var (rig, host, factory, _) = await StartAsync(
            targets: p => $"test=tcp://127.0.0.1:{p},again=tcp://127.0.0.1:{p},elsewhere=tcp://localhost:{p}");
        await using var _host = host;
        using var _factory = factory;
        Assert.Equal("GG1001", (await rig.OpenOkAsync("test")).Device);
        // The same IBM i through another connection, and another user: the next numbers.
        Assert.Equal("GG1002", (await rig.OpenOkAsync("again")).Device);
        Assert.Equal("GG1003", (await rig.OpenOkAsync("test", upn: "second@example.com")).Device);
        // Another host name is another system, numbered on its own.
        Assert.Equal("GG1001", (await rig.OpenOkAsync("elsewhere")).Device);
    }

    [Fact]
    public async Task ANumberFreedBeforeSignOnIsReusedAtOnce()
    {
        var (rig, host, factory, _) = await StartAsync();
        await using var _host = host;
        using var _factory = factory;
        var first = await rig.OpenOkAsync();
        var second = await rig.OpenOkAsync();
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync(first.Id));
        await Wait.UntilAsync(() => host.Open == 1, "the first host connection to drop");
        // Anyone may have it: no job was left on the IBM i.
        Assert.Equal(first.Device, (await rig.OpenOkAsync(upn: "second@example.com")).Device);
        Assert.NotEqual(second.Device, first.Device);
    }

    [Fact]
    public async Task ASessionEndedSignedOnKeepsItsNameForItsUserForFiveMinutes()
    {
        var (rig, host, factory, clock) = await StartAsync();
        await using var _host = host;
        using var _factory = factory;
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using (browser)
        {
            await browser.WaitForTextAsync("Sign On");
            await browser.SignOnAsync("pat", "pw");
            await browser.WaitForTextAsync("IBM i Main Menu");
        }
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync(opened.Id));
        await rig.WaitNoSessionsAsync();

        // The IBM i keeps the disconnected job at GG1001: no one else gets it, and its user does.
        Assert.Equal("GG1002", (await rig.OpenOkAsync(upn: "second@example.com")).Device);
        Assert.Equal("GG1001", (await rig.OpenOkAsync()).Device);
    }

    [Fact]
    public async Task AKeptNameIsFreeForAnyoneAfterFiveMinutes()
    {
        var (rig, host, factory, clock) = await StartAsync();
        await using var _host = host;
        using var _factory = factory;
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using (browser)
        {
            await browser.WaitForTextAsync("Sign On");
            await browser.SignOnAsync("pat", "pw");
            await browser.WaitForTextAsync("IBM i Main Menu");
        }
        await rig.DeleteAsync(opened.Id);
        await rig.WaitNoSessionsAsync();
        clock.Advance(SessionManager.KeptForReconnect);
        Assert.Equal("GG1001", (await rig.OpenOkAsync(upn: "second@example.com")).Device);
    }

    [Fact]
    public async Task ANameInUseIsKeptForTryAgainAndASpareSkipsIt()
    {
        var (rig, host, factory, _) = await StartAsync(maxSessionsPerUser: 5);
        await using var _host = host;
        using var _factory = factory;
        await using var holder = await HostHolder.ConnectAsync(host, "GG1001");
        var inUse = await rig.OpenOkAsync();
        Assert.Equal("GG1001", inUse.Device);
        await rig.WaitNoSessionsAsync();

        Assert.Equal("GG1002", (await rig.OpenOkAsync(upn: "second@example.com")).Device);
        Assert.Equal("GG1003", (await rig.OpenOkAsync(spare: true)).Device);
        // Try again: the same name.
        Assert.Equal("GG1001", (await rig.OpenOkAsync()).Device);
    }

    [Fact]
    public async Task ADrainingNodeOpensNoSessionsAndKeepsThoseItHas()
    {
        var (rig, host, factory, _) = await StartAsync();
        await using var _host = host;
        using var _factory = factory;
        var first = await rig.OpenOkAsync();
        factory.Services.GetRequiredService<ClusterNode>().SetDraining(true);
        var refused = await rig.OpenAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
        Assert.Equal("draining", refused.Error);
        Assert.Equal(first.Id, (await rig.ListAsync()).Sessions.Single().GetProperty("id").GetGuid());

        factory.Services.GetRequiredService<ClusterNode>().SetDraining(false);
        Assert.Equal("GG1002", (await rig.OpenOkAsync()).Device);
    }
}
