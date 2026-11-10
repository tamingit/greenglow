using System.Net;
using System.Text.Json;
using GreenGlow.FakeHost;

namespace GreenGlow.Server.Tests;

/// <summary>/api/admin/log: who connected, from where, to which system and when.</summary>
public sealed class LogAdminTests
{
    private const string Admin = "boss@example.com";

    private static async Task<(Rig Rig, FakeIbmI Host, RelayFactory Factory)> StartAsync()
    {
        var host = await FakeIbmI.StartAsync();
        var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Entra:InitialAdminUpn"] = Admin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson($"test=tcp://127.0.0.1:{host.Port}"),
        });
        return (Rig.Wrap(host, factory), host, factory);
    }

    private static async Task<List<JsonElement>> LogAsync(RelayFactory factory, string query = "")
    {
        var admin = await factory.SignedInClientAsync(Admin);
        var body = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/log{query}")).RootElement;
        return body.GetProperty("rows").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    [Fact]
    public async Task CurrentAndEndedConnectionsAreListedWithUserAddressSystemAndTimes()
    {
        var (rig, host, factory) = await StartAsync();
        await using var _ = host;
        using var __ = factory;
        var (opened, browser) = await rig.OpenAndAttachAsync(upn: "pat@example.com");
        await browser.WaitForTextAsync("Sign On");

        var rows = await LogAsync(factory);
        var session = rows.Single(r => r.GetProperty("kind").GetString() == "session");
        var frontend = rows.Single(r => r.GetProperty("kind").GetString() == "frontend");
        Assert.Equal(opened.Id, session.GetProperty("sessionId").GetGuid());
        Assert.Equal("pat@example.com", session.GetProperty("upn").GetString());
        Assert.Equal("test", session.GetProperty("connection").GetString());
        Assert.True(session.GetProperty("current").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("endedAt").ValueKind);
        Assert.Equal("Sign On", session.GetProperty("title").GetString());
        Assert.Equal("owner", frontend.GetProperty("role").GetString());
        Assert.False(string.IsNullOrEmpty(frontend.GetProperty("clientIp").GetString() ?? "x"));

        Assert.Equal(2, (await LogAsync(factory, "?current=true")).Count);
        Assert.Single(await LogAsync(factory, "?kind=session"));
        Assert.Equal(2, (await LogAsync(factory, "?q=pat")).Count);
        Assert.Empty(await LogAsync(factory, "?q=nobody"));
        Assert.Empty(await LogAsync(factory, "?connection=other"));

        await browser.CloseAsync();
        await rig.WaitNoSessionsAsync("pat@example.com");
        await Wait.UntilAsync(async () => (await LogAsync(factory, "?current=true")).Count == 0, "the rows to close");
        var ended = (await LogAsync(factory)).Single(r => r.GetProperty("kind").GetString() == "session");
        Assert.NotEqual(JsonValueKind.Null, ended.GetProperty("endedAt").ValueKind);
        Assert.Equal("no browser attached", ended.GetProperty("endReason").GetString());
    }

    [Fact]
    public async Task PagesGoBackInTimeAndPeriodsFilter()
    {
        var (rig, host, factory) = await StartAsync();
        await using var _ = host;
        using var __ = factory;
        for (var i = 0; i < 3; i++) await rig.OpenOkAsync();
        var admin = await factory.SignedInClientAsync(Admin);
        var first = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/log?kind=session&limit=2")).RootElement;
        Assert.Equal(2, first.GetProperty("rows").GetArrayLength());
        var next = first.GetProperty("next").GetString();
        var second = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/log?kind=session&limit=2&before={next}")).RootElement;
        Assert.Equal(1, second.GetProperty("rows").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        Assert.Empty(await LogAsync(factory, $"?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}"));
        Assert.Equal(3, (await LogAsync(factory, $"?kind=session&to={future}")).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/log?kind=nonsense")).StatusCode);
    }

    [Fact]
    public async Task OnlyAdministratorsReadTheLog()
    {
        var (_, host, factory) = await StartAsync();
        await using var _host = host;
        using var _factory = factory;
        var user = await factory.SignedInClientAsync("pat@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/log")).StatusCode);
    }
}
