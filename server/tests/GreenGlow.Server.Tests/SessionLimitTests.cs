using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>How many browsers one host session may have (Sessions.MaxFrontends, default 2).</summary>
public sealed class SessionLimitTests
{
    [Fact]
    public async Task TwoBrowsersByDefaultAndTheThirdIsRefused()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, first) = await rig.OpenAndAttachAsync();
        await using var firstScope = first;
        await first.WaitForTextAsync("Sign On");
        await using var second = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await second.WaitForTextAsync("Sign On");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => BrowserClient.AttachAsync(rig.Factory, opened.Id));
        Assert.Contains("409", error.Message);
        Assert.Equal(2, (await rig.ListAsync()).Sessions.Single().GetProperty("frontends").GetInt32());
    }

    [Fact]
    public async Task AnAdministratorsLimitAppliesToTheNextBrowser()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 1);
        var (opened, first) = await rig.OpenAndAttachAsync();
        await using var firstScope = first;
        await first.WaitForTextAsync("Sign On");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => BrowserClient.AttachAsync(rig.Factory, opened.Id));
        Assert.Contains("409", error.Message);

        // A browser that leaves frees its place.
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 3);
        await using var second = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await using var third = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await third.WaitForTextAsync("Sign On");
    }

    [Fact]
    public async Task TheOwnerTakesOverASessionInUseElsewhere()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 1);
        var (opened, first) = await rig.OpenAndAttachAsync();
        await using var firstScope = first;
        await first.WaitForTextAsync("Sign On");

        await using var second = await BrowserClient.AttachAsync(rig.Factory, opened.Id, takeOver: true);
        await second.WaitForTextAsync("Sign On");
        var closed = await first.WaitForTypeAsync("closed");
        Assert.Equal(HostSession.TakenOverReason, closed.GetProperty("reason").GetString());
        await first.WaitClosedAsync();
        // The new browser has the keyboard.
        await second.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard");
        await Task.Delay(200);
        Assert.Equal(1, (await rig.ListAsync()).Sessions.Single().GetProperty("frontends").GetInt32());

        // Only the owner can take over: someone else's attach is refused as before.
        var stranger = await Assert.ThrowsAnyAsync<Exception>(() =>
            BrowserClient.AttachAsync(rig.Factory, opened.Id, "stranger@example.com", takeOver: true));
        Assert.Contains("404", stranger.Message);
    }

    [Fact]
    public async Task BrowsersAttachingAtOnceNeverExceedTheLimit()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                return await BrowserClient.AttachAsync(rig.Factory, opened.Id);
            }
            catch (Exception)
            {
                return null;
            }
        }));
        var browsers = attempts.OfType<BrowserClient>().ToList();
        try
        {
            // Some may be accepted as WebSockets and then told the session is full.
            await Wait.UntilAsync(() => browsers.Count(b => !b.Ended) <= 2 && browsers.Any(b => !b.Ended), "the limit to settle");
            await Task.Delay(200);
            Assert.Equal(2, browsers.Count(b => !b.Ended));
            Assert.All(browsers.Where(b => b.Ended), b => Assert.Equal("session full", b.CloseReason));
        }
        finally
        {
            foreach (var browser in browsers) await browser.DisposeAsync();
        }
    }
}
