using System.Net;
using System.Text;
using System.Text.Json;
using GreenGlow.FakeHost;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>
/// Demo mode (Demo:Enabled): guests without accounts, only demo systems, no sharing, directory
/// or administration, and the seed as the whole configuration.
/// </summary>
public sealed class DemoTests
{
    /// <summary>Seed JSON for demo connections ("name=url,...").</summary>
    private static string Seed(string targets, string disabled = "", bool demo = true) =>
        JsonSerializer.Serialize(new
        {
            connections = targets.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('=', 2)).Select(p => new
            {
                name = p[0],
                label = $"Label {p[0]}",
                url = p[1],
                isDemo = demo,
                enabled = !disabled.Split(',').Contains(p[0]),
            }),
        });

    private static Dictionary<string, string?> Settings(string seed, string? dbPath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Auth:DevLogin"] = "false",
            ["Demo:Enabled"] = "true",
            ["Seed:ConnectionsJson"] = seed,
        };
        if (dbPath is not null) settings["Data:DbPath"] = dbPath;
        return settings;
    }

    /// <summary>Starts a guest (POST /auth/guest) and returns the response.</summary>
    private static async Task<HttpResponseMessage> StartGuestAsync(RelayFactory factory, string? origin = T.Origin, string? forwardedFor = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/guest")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" }),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request, T.Token());
    }

    /// <summary>A client signed in as a new guest, sending this site's Origin.</summary>
    private static async Task<HttpClient> GuestAsync(RelayFactory factory)
    {
        var response = await StartGuestAsync(factory);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("greenglow.auth="));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie[..cookie.IndexOf(';')]);
        client.DefaultRequestHeaders.Add("Origin", T.Origin);
        return client;
    }

    private static async Task<List<string>> TargetsAsync(HttpClient client)
    {
        var body = JsonDocument.Parse(await client.GetStringAsync("/api/targets", T.Token())).RootElement;
        return body.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    private static Exception StartFails(Dictionary<string, string?> settings)
    {
        using var factory = new RelayFactory(settings);
        return Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    private static string Message(Exception error) => (error as ConfigException ?? error.InnerException)?.Message ?? error.Message;

    [Fact]
    public void DemoModeRefusesAConnectionThatIsntADemoSystem()
    {
        var error = StartFails(Settings(Seed("demo=tcp://127.0.0.1:2323", demo: false)));
        Assert.Contains("allows only demo systems", Message(error));
    }

    [Fact]
    public void DemoModeRefusesAnAccessList()
    {
        var seed = """{"connections":[{"name":"demo","url":"tcp://127.0.0.1:2323","isDemo":true,"access":[{"type":"group","name":"Everyone"}]}]}""";
        Assert.Contains("takes no access list", Message(StartFails(Settings(seed))));
    }

    [Theory]
    [InlineData("Entra:TenantId", "00000000-0000-0000-0000-000000000001", "Entra ID")]
    [InlineData("Auth:DevLogin", "true", "dev login")]
    [InlineData("Entra:InitialAdminUpn", "admin@example.com", "no administrators")]
    public void DemoModeRefusesOtherWaysIn(string key, string value, string expected)
    {
        var settings = Settings(Seed("demo=tcp://127.0.0.1:2323"));
        settings[key] = value;
        if (key == "Entra:TenantId") settings["Entra:ClientId"] = "00000000-0000-0000-0000-000000000002";
        Assert.Contains(expected, Message(StartFails(settings)));
    }

    [Fact]
    public void DemoModeNeedsItsConnectionsInTheSeed()
    {
        Assert.Contains("needs its connections", Message(StartFails(Settings(""))));
    }

    [Fact]
    public async Task SigningInGoesToTheDemoPageAndAGuestPostNeedsAnAllowedOrigin()
    {
        using var factory = new RelayFactory(Settings(Seed("demo=tcp://127.0.0.1:2323")));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var signIn = await client.GetAsync("/auth/signin?returnUrl=%2F", T.Token());
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/demo.html?returnUrl=%2F", signIn.Headers.Location?.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, (await StartGuestAsync(factory, origin: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartGuestAsync(factory, origin: "https://evil.example")).StatusCode);
        // The dev login isn't there in demo mode.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/dev", T.Token())).StatusCode);
    }

    [Fact]
    public async Task AGuestSeesOnlyEnabledDemoSystemsAndOpensASession()
    {
        await using var host = await FakeIbmI.StartAsync();
        using var factory = new RelayFactory(Settings(Seed($"demo=tcp://127.0.0.1:{host.Port},pub400=tls://pub400.example:992", disabled: "pub400")));
        var guest = await GuestAsync(factory);

        var me = JsonDocument.Parse(await guest.GetStringAsync("/api/me", T.Token())).RootElement;
        Assert.True(me.GetProperty("demo").GetBoolean());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.StartsWith("Guest ", me.GetProperty("name").GetString());
        Assert.EndsWith("@guest.invalid", me.GetProperty("upn").GetString());

        Assert.Equal(["demo"], await TargetsAsync(guest));
        var opened = await guest.PostAsync("/api/sessions",
            new StringContent("""{"target":"demo"}""", Encoding.UTF8, "application/json"), T.Token());
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var disabled = await guest.PostAsync("/api/sessions",
            new StringContent("""{"target":"pub400"}""", Encoding.UTF8, "application/json"), T.Token());
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    [Fact]
    public async Task SharingTheDirectoryAndAdministrationAreNotThere()
    {
        await using var host = await FakeIbmI.StartAsync();
        using var factory = new RelayFactory(Settings(Seed($"demo=tcp://127.0.0.1:{host.Port}")));
        var guest = await GuestAsync(factory);
        var opened = await guest.PostAsync("/api/sessions",
            new StringContent("""{"target":"demo"}""", Encoding.UTF8, "application/json"), T.Token());
        var id = JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var share = await guest.PostAsync($"/api/sessions/{id}/shares",
            new StringContent("""{"upn":"someone@example.com","role":"view"}""", Encoding.UTF8, "application/json"), T.Token());
        Assert.Equal(HttpStatusCode.NotFound, share.StatusCode);
        foreach (var path in new[] { $"/api/sessions/{id}/shares", "/api/directory/users?q=a", "/api/directory/my-groups",
                     "/api/admin/connections", "/api/admin/guide" })
            Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path, T.Token())).StatusCode);

        // Macros work, but not sharing them.
        var macro = await guest.PostAsync("/api/macros",
            new StringContent("""{"name":"m","description":"Placeholder"}""", Encoding.UTF8, "application/json"), T.Token());
        Assert.Equal(HttpStatusCode.Created, macro.StatusCode);
        var macroId = JsonDocument.Parse(await macro.Content.ReadAsStringAsync()).RootElement.GetProperty("macro").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/macros/{macroId}/shares", T.Token())).StatusCode);
    }

    [Fact]
    public async Task OneGuestCantOpenAnothersSession()
    {
        await using var host = await FakeIbmI.StartAsync();
        using var factory = new RelayFactory(Settings(Seed($"demo=tcp://127.0.0.1:{host.Port}")));
        var first = await GuestAsync(factory);
        var opened = await first.PostAsync("/api/sessions",
            new StringContent("""{"target":"demo"}""", Encoding.UTF8, "application/json"), T.Token());
        var id = JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var second = await StartGuestAsync(factory);
        var cookie = second.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("greenglow.auth="));
        var socket = factory.Server.CreateWebSocketClient();
        socket.ConfigureRequest = r =>
        {
            r.Headers["Origin"] = T.Origin;
            r.Headers["Cookie"] = cookie[..cookie.IndexOf(';')];
        };
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            socket.ConnectAsync(new Uri($"ws://localhost/ws/session/{id}?share=anything"), T.Token()));
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task NewGuestsAreLimitedPerAddress()
    {
        var settings = Settings(Seed("demo=tcp://127.0.0.1:2323"));
        settings["RateLimit:Enabled"] = "true";
        settings["Demo:GuestsPerHour"] = "2";
        settings["Server:TrustForwardedHeaders"] = "true";
        using var factory = new RelayFactory(settings);
        Assert.Equal(HttpStatusCode.Redirect, (await StartGuestAsync(factory, forwardedFor: "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await StartGuestAsync(factory, forwardedFor: "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await StartGuestAsync(factory, forwardedFor: "203.0.113.7")).StatusCode);
        // Another address still may.
        Assert.Equal(HttpStatusCode.Redirect, (await StartGuestAsync(factory, forwardedFor: "203.0.113.8")).StatusCode);
    }

    [Fact]
    public async Task TheSeedIsTheWholeConfiguration()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-demo-").FullName;
        var dbPath = Path.Combine(dir, "greenglow.db");
        using (var first = new RelayFactory(Settings(Seed("demo=tcp://127.0.0.1:2323,old=tcp://127.0.0.1:2324"), dbPath)))
            Assert.Equal(["demo", "old"], await TargetsAsync(await GuestAsync(first)));

        // Restarted with another seed: "old" goes, "demo" changes, "new" comes, disabled.
        var seed = Seed("new=tcp://127.0.0.1:2325,demo=tcp://127.0.0.1:2329", disabled: "new");
        using var second = new RelayFactory(Settings(seed, dbPath));
        Assert.Equal(["demo"], await TargetsAsync(await GuestAsync(second)));
        await using var db = await second.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        var stored = await db.Connections.OrderBy(c => c.SortOrder).ToListAsync();
        Assert.Equal(["new", "demo"], stored.Select(c => c.Name));
        Assert.False(stored[0].Enabled);
        Assert.Equal(2329, stored[1].Port);
        Assert.All(stored, c => Assert.True(c.IsDemo));
    }

    [Fact]
    public async Task OldGuestsWithoutASessionArePruned()
    {
        await using var host = await FakeIbmI.StartAsync();
        using var factory = new RelayFactory(Settings(Seed($"demo=tcp://127.0.0.1:{host.Port}")));
        var busy = await GuestAsync(factory);
        await busy.PostAsync("/api/sessions", new StringContent("""{"target":"demo"}""", Encoding.UTF8, "application/json"), T.Token());
        var idle = await GuestAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await idle.GetAsync("/api/me", T.Token())).StatusCode);

        var users = factory.Services.GetRequiredService<UserStore>();
        var sessions = factory.Services.GetRequiredService<SessionManager>();
        // Everyone is "old" an hour from now; only the guest holding a session stays.
        var removed = await users.PruneGuestsAsync(DateTimeOffset.UtcNow.AddHours(1), sessions.All.Select(s => s.Owner.Id).ToHashSet());
        Assert.Equal(1, removed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.GetAsync("/api/me", T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await busy.GetAsync("/api/me", T.Token())).StatusCode);
    }

    [Fact]
    public async Task DemoModeStartsInProductionWithoutEntra()
    {
        using var factory = new RelayFactory(Settings(Seed("demo=tcp://127.0.0.1:2323")));
        factory.Environment = "Production";
        Assert.Equal(["demo"], await TargetsAsync(await GuestAsync(factory)));
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("Demo mode is on"));
    }
}
