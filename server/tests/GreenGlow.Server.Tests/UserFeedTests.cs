using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>/ws/user: the server pushes each page its user's session list when it changes.</summary>
public sealed class UserFeedTests
{
    private const string Pat = "pat@example.com";
    private const string Sam = "sam@example.com";
    private const string Lee = "lee@example.com";

    private static async Task<BrowserClient> FeedAsync(Rig rig, string upn = RelayFactory.DefaultUpn)
    {
        var feed = new BrowserClient(await rig.Factory.ConnectUserAsync(upn));
        // The list comes at once.
        await feed.WaitForTypeAsync("sessions");
        return feed;
    }

    private static Task<JsonElement> ListAsync(BrowserClient feed, Func<JsonElement[], bool> match, string what, int since) =>
        feed.WaitForAsync(m => BrowserClient.TypeOf(m) == "sessions" && match([.. m.GetProperty("sessions").EnumerateArray()]),
            what, since);

    [Fact]
    public async Task AnonymousIs401AndAForeignOriginIs403()
    {
        await using var rig = await Rig.StartAsync();
        var anonymous = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectUserAsync(upn: null));
        Assert.Contains("401", anonymous.Message);
        var foreign = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectUserAsync(origin: "http://evil.example.com"));
        Assert.Contains("403", foreign.Message);
        var none = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectUserAsync(origin: null));
        Assert.Contains("403", none.Message);
    }

    [Fact]
    public async Task ASessionOpeningSigningOnAndEndingEachPushToItsOwnerOnly()
    {
        await using var rig = await Rig.StartAsync();
        await using var mine = await FeedAsync(rig);
        await using var other = await FeedAsync(rig, Sam);
        Assert.Empty(mine.Messages[0].GetProperty("sessions").EnumerateArray());

        var mark = mine.Mark;
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await ListAsync(mine, l => l.Length == 1 && l[0].GetProperty("id").GetGuid() == opened.Id, "the new session", mark);

        mark = mine.Mark;
        await browser.WaitForTextAsync("Sign On");
        await browser.SignOnAsync("pat", "pw");
        await ListAsync(mine, l => l.Length == 1 && l[0].GetProperty("signedOn").GetBoolean()
                                   && l[0].GetProperty("title").GetString() == "IBM i Main Menu", "signed on, with the menu's title", mark);

        mark = mine.Mark;
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync(opened.Id));
        await ListAsync(mine, l => l.Length == 0, "the session gone", mark);

        // Someone else's page heard nothing of it.
        await Task.Delay(UserFeedBatch * 2);
        Assert.Single(other.Messages);
    }

    private static readonly TimeSpan UserFeedBatch = GreenGlow.Server.Sessions.UserFeed.Batch;

    [Fact]
    public async Task ShareChangesReachThePeopleNamedAndNoOneElse()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        await using var pat = await FeedAsync(rig, Pat);
        await using var sam = await FeedAsync(rig, Sam);
        await using var lee = await FeedAsync(rig, Lee);
        var client = await rig.Factory.SignedInClientAsync();

        // Shared: Pat and Sam see it at once.
        int patMark = pat.Mark, samMark = sam.Mark;
        var created = await client.PostAsJsonAsync($"/api/sessions/{opened.Id}/shares",
            new { recipients = new[] { new { upn = Pat, access = "view" }, new { upn = Sam, access = "view" } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var share = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.Clone();
        await ListAsync(pat, l => l.Length == 1 && l[0].GetProperty("role").GetString() == "view", "Pat's share", patMark);
        await ListAsync(sam, l => l.Length == 1, "Sam's share", samMark);
        var path = (string upn) =>
            $"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}/recipients/" +
            share.GetProperty("recipients").EnumerateArray().Single(r => r.GetProperty("upn").GetString() == upn).GetProperty("id").GetGuid();

        // Pat's access changed: Pat sees it.
        patMark = pat.Mark;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(path(Pat), new { access = "full" })).StatusCode);
        await ListAsync(pat, l => l.Length == 1 && l[0].GetProperty("role").GetString() == "full", "Pat's full access", patMark);

        // Sam removed: Sam's list empties.
        samMark = sam.Mark;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path(Sam))).StatusCode);
        await ListAsync(sam, l => l.Length == 0, "Sam's list empty", samMark);

        // The share revoked: Pat's list empties.
        patMark = pat.Mark;
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}")).StatusCode);
        await ListAsync(pat, l => l.Length == 0, "Pat's list empty", patMark);

        // Lee, never named, heard nothing but their own list at the start.
        await Task.Delay(UserFeedBatch * 2);
        Assert.Single(lee.Messages);
        Assert.Empty(lee.Messages[0].GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task APageWithTheFeedOpenCountsAsInGreenGlow()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 3);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        var client = await rig.Factory.SignedInClientAsync();
        await client.PostAsJsonAsync($"/api/sessions/{opened.Id}/shares",
            new { recipients = new[] { new { upn = Pat, access = "view" } } });
        await using var pat = await FeedAsync(rig, Pat);
        var shares = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{opened.Id}/shares")).RootElement;
        var person = shares.GetProperty("shares")[0].GetProperty("recipients")[0];
        Assert.True(person.GetProperty("inGreenGlow").GetBoolean());
    }
}
