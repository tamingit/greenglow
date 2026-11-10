using GreenGlow.FakeHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

/// <summary>A session with no browser stays open for Sessions.IdleMinutes, then ends.</summary>
public sealed class IdleTests
{
    private static async Task<(Rig Rig, FakeTimeProvider Clock, FakeIbmI Host, RelayFactory Factory)> StartAsync(int minutes)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = await FakeIbmI.StartAsync();
        var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson($"test=tcp://127.0.0.1:{host.Port}"),
            ["Sessions:IdleMinutes"] = minutes.ToString(),
        })
        {
            ExtraServices = s => s.AddSingleton<TimeProvider>(clock),
        };
        return (Rig.Wrap(host, factory), clock, host, factory);
    }

    [Fact]
    public async Task ASessionWithNoBrowserEndsAfterTheIdleTime()
    {
        var (rig, clock, host, factory) = await StartAsync(5);
        await using var _ = host;
        using var __ = factory;
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await browser.WaitForTextAsync("Sign On");
        await browser.CloseAsync();

        await Wait.UntilAsync(async () => (await rig.ListAsync()).Sessions.Single().GetProperty("frontends").GetInt32() == 0, "the browser to leave");
        var listed = (await rig.ListAsync()).Sessions.Single();
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, listed.GetProperty("idleSince").ValueKind);

        clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(200);
        Assert.Equal(opened.Id, (await rig.ListAsync()).Sessions.Single().GetProperty("id").GetGuid());
        Assert.Equal(1, host.Open);

        clock.Advance(TimeSpan.FromMinutes(1.5));
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(() => host.Open == 0, "the host connection to close");
        var row = (await rig.LogRowsAsync(Data.LogKind.Session)).Single();
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync(Data.LogKind.Session)).Single().EndedAt is not null, "the log row");
        Assert.Equal("no browser attached for 5 minutes", (await rig.LogRowsAsync(Data.LogKind.Session)).Single().EndReason);
        Assert.Equal(opened.Id, row.SessionId);
    }

    [Fact]
    public async Task ABrowserThatComesBackInTimeKeepsTheSessionAndItsScreen()
    {
        var (rig, clock, host, factory) = await StartAsync(5);
        await using var _ = host;
        using var __ = factory;
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await browser.WaitForTextAsync("Sign On");
        await browser.SignOnAsync("pat", "pw");
        await browser.WaitForTextAsync("IBM i Main Menu");
        await browser.CloseAsync();
        await Wait.UntilAsync(async () => (await rig.ListAsync()).Sessions.Single().GetProperty("frontends").GetInt32() == 0, "the browser to leave");

        clock.Advance(TimeSpan.FromMinutes(3));
        await using var back = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await back.WaitForTextAsync("IBM i Main Menu");

        // The earlier timer no longer applies, even well past it.
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(200);
        Assert.Single((await rig.ListAsync()).Sessions);
        Assert.Equal(1, host.Open);
    }

    [Fact]
    public async Task ASessionNoBrowserEverAttachesToEndsToo()
    {
        var (rig, clock, host, factory) = await StartAsync(5);
        await using var _ = host;
        using var __ = factory;
        await rig.OpenOkAsync();
        await host.WaitForConnectionsAsync(1, T.Timeout);
        await Wait.UntilAsync(
            async () => (await rig.ListAsync()).Sessions.Single().GetProperty("idleSince").ValueKind != System.Text.Json.JsonValueKind.Null,
            "the idle timer to start");
        clock.Advance(TimeSpan.FromMinutes(6));
        await rig.WaitNoSessionsAsync();
    }

    [Fact]
    public async Task ZeroMinutesEndsTheSessionAtOnce()
    {
        var (rig, _, host, factory) = await StartAsync(0);
        await using var __ = host;
        using var ___ = factory;
        var (_, browser) = await rig.OpenAndAttachAsync();
        await browser.WaitForTextAsync("Sign On");
        await browser.CloseAsync();
        await rig.WaitNoSessionsAsync();
    }
}
