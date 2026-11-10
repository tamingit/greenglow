using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;
using GreenGlow.FakeHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

/// <summary>Sharing a live session with named people, view-only or with full access.</summary>
public sealed class ShareTests
{
    private const string Pat = "pat@example.com";
    private const string Sam = "sam@example.com";

    private static async Task<JsonElement> ShareAsync(Rig rig, Guid id, params (string Upn, string Access)[] recipients)
    {
        var owner = await rig.Factory.SignedInClientAsync();
        var response = await owner.PostAsJsonAsync($"/api/sessions/{id}/shares",
            new { recipients = recipients.Select(r => new { upn = r.Upn, access = r.Access }) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string Token(JsonElement share) =>
        Uri.UnescapeDataString(share.GetProperty("url").GetString()!.Split("?share=")[1]);

    private static async Task<BrowserClient> AttachAsync(Rig rig, Guid id, string upn, string token) =>
        new(await rig.Factory.ConnectAsync(id, upn: upn, share: token));

    [Fact]
    public async Task ARecipientOpensTheLinkWithTheAccessTheyWereGiven()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var share = await ShareAsync(rig, opened.Id, (Pat, "view"), (Sam, "full"));
        Assert.StartsWith(T.Origin + "/?share=", share.GetProperty("url").GetString());
        var token = Token(share);

        var pat = await rig.Factory.SignedInClientAsync(Pat);
        var seen = JsonDocument.Parse(await pat.GetStringAsync($"/api/shares/{Uri.EscapeDataString(token)}")).RootElement;
        Assert.Equal(("view", opened.Id), (seen.GetProperty("role").GetString(), seen.GetProperty("id").GetGuid()));

        // Pat's list has the session, with the token to attach with.
        var listed = (await rig.ListAsync(Pat)).Sessions.Single();
        Assert.Equal(("view", token), (listed.GetProperty("role").GetString(), listed.GetProperty("shareToken").GetString()));

        // Someone not named sees nothing, even with the token.
        var stranger = await rig.Factory.SignedInClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/shares/{Uri.EscapeDataString(token)}")).StatusCode);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => AttachAsync(rig, opened.Id, "stranger@example.com", token));
        Assert.Contains("404", refused.Message);
        Assert.Empty((await rig.ListAsync("stranger@example.com")).Sessions);
    }

    [Fact]
    public async Task ViewOnlyWatchesAndFullAccessCanTakeTheKeyboard()
    {
        await using var rig = await Rig.StartAsync();
        // Room for the owner and two recipients.
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 3);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "view"), (Sam, "full")));

        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        var hello = await pat.WaitForTypeAsync("hello");
        Assert.Equal("view", hello.GetProperty("role").GetString());
        await pat.WaitForTextAsync("Sign On");
        var mark = pat.Mark;
        await pat.TypeAsync("x");
        await pat.WaitForTypeAsync("refused", mark);
        await pat.TakeControlAsync();

        await using var sam = await AttachAsync(rig, opened.Id, Sam, token);
        Assert.Equal("full", (await sam.WaitForTypeAsync("hello")).GetProperty("role").GetString());
        mark = sam.Mark;
        await sam.TakeControlAsync();
        await sam.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard", mark);
        await owner.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && !m.GetProperty("you").GetBoolean(), "losing the keyboard");
        await sam.SignOnAsync("pat", "pw");
        await owner.WaitForTextAsync("IBM i Main Menu");
        await pat.WaitForTextAsync("IBM i Main Menu");

        // The view-only browser never got the keyboard.
        Assert.DoesNotContain(pat.Messages, m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean());
        var frontends = (await rig.LogRowsAsync(LogKind.Frontend)).Select(r => r.Role).Order().ToList();
        Assert.Equal(["full", "owner", "view"], frontends);
    }

    [Fact]
    public async Task RevokingSendsTheRecipientsAwayAndTheLinkStopsWorking()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var share = await ShareAsync(rig, opened.Id, (Pat, "full"));
        var token = Token(share);
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");

        var client = await rig.Factory.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}")).StatusCode);
        var closed = await pat.WaitForTypeAsync("closed");
        Assert.Contains("stopped sharing", closed.GetProperty("reason").GetString());
        // A clean close, not a dropped socket, or a browser can lose the message above.
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, (await pat.WaitClosedAsync()).Status);
        var again = await Assert.ThrowsAnyAsync<Exception>(() => AttachAsync(rig, opened.Id, Pat, token));
        Assert.Contains("404", again.Message);
        Assert.Empty((await rig.ListAsync(Pat)).Sessions);
        // The owner's session carries on.
        Assert.Single((await rig.ListAsync()).Sessions);
    }

    [Fact]
    public async Task WhenTheOwnerDisconnectsNobodyCanViewTheSessionUntilTheyAreBack()
    {
        await using var rig = await Rig.StartAsync();
        // Sessions left running wait for their owner, as they do outside tests.
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.IdleMinutes, 5);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "full")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");

        await owner.CloseAsync("user disconnected");
        var closed = await pat.WaitForTypeAsync("closed");
        Assert.Equal(HostSession.OwnerLeftReason, closed.GetProperty("reason").GetString());
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, (await pat.WaitClosedAsync()).Status);

        // While the owner is away the session isn't listed or offered, and attaching is refused.
        Assert.Empty((await rig.ListAsync(Pat)).Sessions);
        var client = await rig.Factory.SignedInClientAsync(Pat);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/shares/{Uri.EscapeDataString(token)}")).StatusCode);
        await using (var refused = await AttachAsync(rig, opened.Id, Pat, token))
            Assert.Equal(HostSession.OwnerAwayReason, (await refused.WaitForTypeAsync("closed")).GetProperty("reason").GetString());
        // The session itself waits for the owner, as any session left running does.
        Assert.Single((await rig.ListAsync()).Sessions);

        await using var back = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await back.WaitForTypeAsync("hello");
        Assert.Single((await rig.ListAsync(Pat)).Sessions);
        await using var again = await AttachAsync(rig, opened.Id, Pat, token);
        await again.WaitForTextAsync("Sign On");
    }

    [Fact]
    public async Task WhenTheOwnersBrowserDropsThePeopleItIsSharedWithStayForAMoment()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await FakeIbmI.StartAsync();
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson($"test=tcp://127.0.0.1:{host.Port}"),
        })
        {
            ExtraServices = s => s.AddSingleton<TimeProvider>(clock),
        };
        await using var rig = Rig.Wrap(host, factory);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "view")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");

        // A reload: the owner's page goes without saying it disconnected, and comes back.
        await owner.CloseAsync();
        await Wait.UntilAsync(async () => (await rig.ListAsync()).Sessions.Single().GetProperty("frontends").GetInt32() == 1, "the owner to leave");
        clock.Advance(HostSession.OwnerGrace / 2);
        await using var back = await BrowserClient.AttachAsync(factory, opened.Id);
        await back.WaitForTypeAsync("hello");
        clock.Advance(HostSession.OwnerGrace);
        await Task.Delay(200);
        Assert.DoesNotContain(pat.Messages, m => BrowserClient.TypeOf(m) == "closed");

        // Gone for good: after the grace, the people it is shared with go too.
        await back.CloseAsync();
        await Wait.UntilAsync(async () => (await rig.ListAsync()).Sessions.Single().GetProperty("frontends").GetInt32() == 1, "the owner to leave again");
        clock.Advance(HostSession.OwnerGrace + TimeSpan.FromSeconds(1));
        var closed = await pat.WaitForTypeAsync("closed");
        Assert.Equal(HostSession.OwnerLeftReason, closed.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ABrowserThatNeverAnswersTheCloseIsLetGoAnyway()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var share = await ShareAsync(rig, opened.Id, (Pat, "view"));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, Token(share));
        pat.AnswerClose = false;
        await pat.WaitForTextAsync("Sign On");
        var session = rig.Factory.Services.GetRequiredService<SessionManager>().Find(opened.Id)!;

        var client = await rig.Factory.SignedInClientAsync();
        await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}");
        await pat.WaitClosedAsync();
        var deadline = DateTime.UtcNow + T.Timeout;
        while (session.Attached.Any(a => a.UserId != session.Owner.Id))
        {
            Assert.True(DateTime.UtcNow < deadline, "the hung browser is still attached");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task RecipientsDontCountTowardTheBrowserLimit()
    {
        await using var rig = await Rig.StartAsync();
        // One browser per session: the owner's only. People it is shared with still get in.
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 1);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "view"), (Sam, "full")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await using var sam = await AttachAsync(rig, opened.Id, Sam, token);
        await pat.WaitForTextAsync("Sign On");
        await sam.WaitForTextAsync("Sign On");

        var listed = (await rig.ListAsync()).Sessions.Single();
        Assert.Equal(3, listed.GetProperty("frontends").GetInt32());
        Assert.Equal(1, listed.GetProperty("ownerFrontends").GetInt32());

        // The owner's own second browser is still one too many.
        var full = await Assert.ThrowsAnyAsync<Exception>(() => BrowserClient.AttachAsync(rig.Factory, opened.Id));
        Assert.Contains("409", full.Message);
    }

    [Theory]
    [InlineData("""{"recipients":[]}""", "recipients")]
    [InlineData("""{"recipients":[{"upn":"pat@example.com","access":"admin"}]}""", "access")]
    [InlineData("""{"recipients":[{"upn":"not-a-upn","access":"view"}]}""", "not-found")]
    [InlineData("""{"recipients":[{"upn":"tester@example.com","access":"view"}]}""", "self")]
    public async Task BadSharesAreRefusedWithTheReason(string body, string error)
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var owner = await rig.Factory.SignedInClientAsync();
        var response = await owner.PostAsync($"/api/sessions/{opened.Id}/shares",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"error\":\"{error}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OnlyTheOwnerSharesOrSeesTheShares()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var other = await rig.Factory.SignedInClientAsync(Pat);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/sessions/{opened.Id}/shares",
            new { recipients = new[] { new { upn = Sam, access = "view" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/sessions/{opened.Id}/shares")).StatusCode);
    }

    private static async Task<List<JsonElement>> RecipientsAsync(Rig rig, Guid id)
    {
        var owner = await rig.Factory.SignedInClientAsync();
        var body = JsonDocument.Parse(await owner.GetStringAsync($"/api/sessions/{id}/shares")).RootElement;
        return body.GetProperty("shares").EnumerateArray()
            .SelectMany(s => s.GetProperty("recipients").EnumerateArray().Select(r => r.Clone())).ToList();
    }

    private static Guid RecipientId(List<JsonElement> recipients, string upn) =>
        recipients.Single(r => r.GetProperty("upn").GetString() == upn).GetProperty("id").GetGuid();

    [Fact]
    public async Task TheOwnerSeesEachPersonTheirAccessAndWhoIsWatching()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "view"), (Sam, "full")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");

        var recipients = await RecipientsAsync(rig, opened.Id);
        Assert.Equal([(Pat, "view", true), (Sam, "full", false)],
            recipients.Select(r => (r.GetProperty("upn").GetString(), r.GetProperty("access").GetString(), r.GetProperty("watching").GetBoolean())));
    }

    [Fact]
    public async Task TheOwnerSeesWhoHasTheKeyboardAndWhoHasGreenGlowOpen()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 3);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "full"), (Sam, "view"), ("lee@example.com", "view")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");
        // Sam has the page open (it asks for Sam's sessions) but hasn't joined; Lee hasn't either.
        await rig.ListAsync(Sam);

        var mark = pat.Mark;
        await pat.TakeControlAsync();
        await pat.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard", mark);
        var recipients = await RecipientsAsync(rig, opened.Id);
        Assert.Equal([("lee@example.com", false, false, false), (Pat, true, true, true), (Sam, false, false, true)],
            recipients.Select(r => (r.GetProperty("upn").GetString(), r.GetProperty("watching").GetBoolean(),
                r.GetProperty("keyboard").GetBoolean(), r.GetProperty("inGreenGlow").GetBoolean())));

        // Pat gives the keyboard back to the owner.
        mark = owner.Mark;
        await pat.ReturnControlAsync();
        await owner.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard back", mark);
        Assert.DoesNotContain(await RecipientsAsync(rig, opened.Id), r => r.GetProperty("keyboard").GetBoolean());
    }

    [Fact]
    public async Task SomeoneInASharedSessionSeesWhoElseItIsSharedWithButNoLinks()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "view"), (Sam, "full")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");

        var client = await rig.Factory.SignedInClientAsync(Pat);
        var raw = await client.GetStringAsync($"/api/sessions/{opened.Id}/people");
        Assert.DoesNotContain(token, raw);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(RelayFactory.DefaultUpn, body.GetProperty("owner").GetProperty("upn").GetString());
        Assert.True(body.GetProperty("owner").GetProperty("keyboard").GetBoolean());
        Assert.Equal([(Pat, "view", true, true), (Sam, "full", false, false)],
            body.GetProperty("people").EnumerateArray().Select(p => (p.GetProperty("upn").GetString(),
                p.GetProperty("access").GetString(), p.GetProperty("watching").GetBoolean(), p.GetProperty("you").GetBoolean())));

        // Someone it isn't shared with sees nothing.
        var stranger = await rig.Factory.SignedInClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/sessions/{opened.Id}/people")).StatusCode);
    }

    [Fact]
    public async Task SomeoneASessionIsSharedWithTakesItOffTheirList()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var token = Token(await ShareAsync(rig, opened.Id, (Pat, "full")));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await pat.WaitForTextAsync("Sign On");

        var client = await rig.Factory.SignedInClientAsync(Pat);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/me")).StatusCode);
        Assert.Contains("stopped sharing", (await pat.WaitForTypeAsync("closed")).GetProperty("reason").GetString());
        Assert.Empty((await rig.ListAsync(Pat)).Sessions);
        // Pat was its only person, so the link is revoked; the owner's session carries on.
        Assert.Empty(await RecipientsAsync(rig, opened.Id));
        Assert.Single((await rig.ListAsync()).Sessions);
        // Again, or by someone it never named: nothing to leave.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/me")).StatusCode);
    }

    [Fact]
    public async Task MakingSomeoneViewOnlyTakesTheKeyboardFromThemAtOnce()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var share = await ShareAsync(rig, opened.Id, (Sam, "full"));
        await using var sam = await AttachAsync(rig, opened.Id, Sam, Token(share));
        var mark = sam.Mark;
        await sam.TakeControlAsync();
        await sam.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard", mark);

        var client = await rig.Factory.SignedInClientAsync();
        var samId = RecipientId(await RecipientsAsync(rig, opened.Id), Sam);
        mark = sam.Mark;
        var ownerMark = owner.Mark;
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await client.PatchAsJsonAsync($"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}/recipients/{samId}",
                new { access = "view" })).StatusCode);
        var role = await sam.WaitForAsync(m => BrowserClient.TypeOf(m) == "role", "the new role", mark);
        Assert.Equal("view", role.GetProperty("role").GetString());
        await owner.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard back", ownerMark);
        mark = sam.Mark;
        await sam.TypeAsync("x");
        await sam.WaitForTypeAsync("refused", mark);
        Assert.Equal("view", (await RecipientsAsync(rig, opened.Id)).Single().GetProperty("access").GetString());
    }

    [Fact]
    public async Task GivingAViewerFullAccessLetsThemTakeTheKeyboard()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var share = await ShareAsync(rig, opened.Id, (Pat, "view"));
        await using var pat = await AttachAsync(rig, opened.Id, Pat, Token(share));
        await pat.WaitForTextAsync("Sign On");
        var client = await rig.Factory.SignedInClientAsync();
        var patId = RecipientId(await RecipientsAsync(rig, opened.Id), Pat);
        var mark = pat.Mark;
        await client.PatchAsJsonAsync($"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}/recipients/{patId}",
            new { access = "full" });
        Assert.Equal("full", (await pat.WaitForAsync(m => BrowserClient.TypeOf(m) == "role", "the new role", mark)).GetProperty("role").GetString());
        mark = pat.Mark;
        await pat.TakeControlAsync();
        await pat.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "the keyboard", mark);
    }

    [Fact]
    public async Task RemovingOnePersonLeavesTheOthersAndTheLastOneRevokesTheLink()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Factory.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.MaxFrontendsPerSession, 3);
        var (opened, owner) = await rig.OpenAndAttachAsync();
        await using var ownerScope = owner;
        await owner.WaitForTextAsync("Sign On");
        var share = await ShareAsync(rig, opened.Id, (Pat, "view"), (Sam, "view"));
        var shareId = share.GetProperty("id").GetGuid();
        var token = Token(share);
        await using var pat = await AttachAsync(rig, opened.Id, Pat, token);
        await using var sam = await AttachAsync(rig, opened.Id, Sam, token);
        await pat.WaitForTextAsync("Sign On");
        await sam.WaitForTextAsync("Sign On");

        var client = await rig.Factory.SignedInClientAsync();
        var recipients = await RecipientsAsync(rig, opened.Id);
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/{shareId}/recipients/{RecipientId(recipients, Pat)}")).StatusCode);
        Assert.Contains("stopped sharing this session with you", (await pat.WaitForTypeAsync("closed")).GetProperty("reason").GetString());
        Assert.False(sam.Ended);
        Assert.Equal([Sam], (await RecipientsAsync(rig, opened.Id)).Select(r => r.GetProperty("upn").GetString()));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => AttachAsync(rig, opened.Id, Pat, token));
        Assert.Contains("404", refused.Message);

        await client.DeleteAsync($"/api/sessions/{opened.Id}/shares/{shareId}/recipients/{RecipientId(recipients, Sam)}");
        await sam.WaitForTypeAsync("closed");
        Assert.Empty(await RecipientsAsync(rig, opened.Id));
        Assert.Empty((await rig.ListAsync(Sam)).Sessions);
    }

    [Fact]
    public async Task OnlyTheOwnerChangesOrRemovesPeopleWithAValidAccess()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var share = await ShareAsync(rig, opened.Id, (Pat, "view"));
        var path = $"/api/sessions/{opened.Id}/shares/{share.GetProperty("id").GetGuid()}/recipients/{RecipientId(await RecipientsAsync(rig, opened.Id), Pat)}";
        var other = await rig.Factory.SignedInClientAsync(Sam);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await other.PatchAsJsonAsync(path, new { access = "full" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await other.DeleteAsync(path)).StatusCode);
        var owner = await rig.Factory.SignedInClientAsync();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(path, new { access = "admin" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await owner.PatchAsJsonAsync(path.Replace("/recipients/", "/recipients/9"), new { access = "full" })).StatusCode);
    }
}
