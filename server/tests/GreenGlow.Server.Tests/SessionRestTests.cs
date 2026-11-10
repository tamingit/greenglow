using System.Net;
using System.Text;

namespace GreenGlow.Server.Tests;

public sealed class SessionRestTests
{
    [Fact]
    public async Task ApiNeedsASignIn()
    {
        await using var rig = await Rig.StartAsync();
        var anonymous = rig.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions", T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/sessions/{Guid.NewGuid()}", T.Token())).StatusCode);
    }

    [Fact]
    public async Task UnknownTargetIs404AndNothingConnects()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenAsync("nope");
        Assert.Equal(HttpStatusCode.NotFound, opened.Status);
        Assert.Equal("unknown-target", opened.Error);
        Assert.Equal(0, rig.Host.Connections);
        Assert.Empty((await rig.ListAsync()).Sessions);
    }

    [Fact]
    public async Task DisabledTargetIs404()
    {
        await using var rig = await Rig.StartAsync(targets: p => $"test=tcp://127.0.0.1:{p},other=tcp://127.0.0.1:{p}");
        await using (var db = rig.Db())
        {
            db.Connections.Single(c => c.Name == "other").Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await rig.OpenAsync("other")).Status);
        Assert.Equal(HttpStatusCode.Created, (await rig.OpenAsync("test")).Status);
    }

    [Fact]
    public async Task ABodyWithoutATargetIsRefused()
    {
        await using var rig = await Rig.StartAsync();
        var client = await rig.Factory.SignedInClientAsync();
        var response = await client.PostAsync("/api/sessions", new StringContent("{}", Encoding.UTF8, "application/json"), T.Token());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpeningASessionReturns201AndConnectsToTheHost()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenAsync();
        Assert.Equal(HttpStatusCode.Created, opened.Status);
        var body = opened.Body;
        Assert.NotEqual(Guid.Empty, opened.Id);
        Assert.Equal("test", body.GetProperty("target").GetString());
        Assert.False(body.GetProperty("secure").GetBoolean());
        Assert.Equal(RelayFactory.DefaultUpn, body.GetProperty("owner").GetString());
        Assert.Equal("owner", body.GetProperty("role").GetString());
        Assert.False(body.GetProperty("signedOn").GetBoolean());
        await Wait.UntilAsync(() => rig.Host.Open == 1, "the host connection");
    }

    [Fact]
    public async Task TheServerWideSessionLimitIsRefused()
    {
        await using var rig = await Rig.StartAsync(max: 2);
        await rig.OpenOkAsync();
        await rig.OpenOkAsync(upn: "second@example.com");
        var refused = await rig.OpenAsync(upn: "third@example.com");
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("server-full", refused.Error);
        var mine = await rig.OpenAsync();
        Assert.Equal("server-full", mine.Error);
    }

    [Fact]
    public async Task EndingASessionFreesAServerSlot()
    {
        await using var rig = await Rig.StartAsync(max: 1);
        var first = await rig.OpenOkAsync();
        Assert.Equal("server-full", (await rig.OpenAsync()).Error);
        await rig.DeleteAsync(first.Id);
        await Wait.UntilAsync(async () => (await rig.OpenAsync()).Status == HttpStatusCode.Created, "a free slot");
    }

    [Fact]
    public async Task DeleteEndsTheSessionAndDropsTheHostConnection()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        await Wait.UntilAsync(() => rig.Host.Open == 1, "the host connection");
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync(opened.Id));
        await Wait.UntilAsync(() => rig.Host.Open == 0, "the host connection to drop");
        await rig.WaitNoSessionsAsync();
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync(opened.Id));

        await Wait.UntilAsync(async () => (await rig.LogRowsAsync(Data.LogKind.Session)).Single().EndedAt is not null, "the log row to close");
        Assert.Equal("ended by the user", (await rig.LogRowsAsync(Data.LogKind.Session)).Single().EndReason);
    }

    [Fact]
    public async Task DeleteOfAnUnknownSessionIs404()
    {
        await using var rig = await Rig.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync(Guid.NewGuid()));
        var client = await rig.Factory.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/sessions/not-a-guid", T.Token())).StatusCode);
    }

    [Fact]
    public async Task ChangesNeedAnAllowedOrigin()
    {
        await using var rig = await Rig.StartAsync();
        var client = await rig.Factory.SignedInClientAsync();
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        var response = await client.PostAsync("/api/sessions",
            new StringContent("""{"target":"test"}""", Encoding.UTF8, "application/json"), T.Token());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/sessions/{Guid.NewGuid()}", T.Token())).StatusCode);
        Assert.Equal(0, rig.Host.Connections);
    }

    [Fact]
    public async Task ListsAreNoStoreAndNeverShowTheHost()
    {
        await using var rig = await Rig.StartAsync();
        await rig.OpenOkAsync();
        var client = await rig.Factory.SignedInClientAsync();
        var response = await client.GetAsync("/api/sessions", T.Token());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("127.0.0.1", raw);
        Assert.DoesNotContain(rig.Host.Port.ToString(), raw);
    }

    [Fact]
    public async Task AnotherUserCannotSeeAttachToOrDeleteASession()
    {
        await using var rig = await Rig.StartAsync();
        var mine = await rig.OpenOkAsync();
        await rig.Factory.SignInAsync("intruder@example.com");

        Assert.Single((await rig.ListAsync()).Sessions);
        Assert.Empty((await rig.ListAsync("intruder@example.com")).Sessions);
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync(mine.Id, "intruder@example.com"));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectAsync(mine.Id, upn: "intruder@example.com"));
        Assert.Contains("404", error.Message);

        var still = Assert.Single((await rig.ListAsync()).Sessions);
        Assert.Equal(mine.Id, still.GetProperty("id").GetGuid());
        Assert.Equal(1, rig.Host.Open);
    }

    [Fact]
    public async Task EachSessionIsLoggedWithTheUser()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync(upn: "logged@example.com");
        var row = Assert.Single(await rig.LogRowsAsync());
        Assert.Equal(Data.LogKind.Session, row.Kind);
        Assert.Equal(opened.Id, row.SessionId);
        Assert.Equal("logged@example.com", row.Upn);
        Assert.Equal(Auth.AuthSetup.DevObjectId("logged@example.com"), row.UserId);
        Assert.Equal("test", row.ConnectionName);
        Assert.Equal(opened.Device, row.DeviceName);
        Assert.Null(row.EndedAt);
    }
}
