using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.FakeHost;

namespace GreenGlow.Server.Tests;

/// <summary>
/// The per-user rate limits: directory search (so Entra ID can't be enumerated quickly) and
/// printer sign-on (so an IBM i password can't be guessed quickly). RateLimit:Enabled is off in
/// the test host by default; these tests turn it on with low limits.
/// </summary>
public sealed class RateLimitTests
{
    private static RelayFactory Factory(params (string Key, string Value)[] settings)
    {
        var dict = new Dictionary<string, string?> { ["Server:PublicUrl"] = T.Origin, ["RateLimit:Enabled"] = "true" };
        foreach (var (key, value) in settings) dict[key] = value;
        return new RelayFactory(dict);
    }

    [Fact]
    public async Task DirectorySearchIsLimitedPerUserAndReturns429WithRetryAfter()
    {
        using var factory = Factory(("RateLimit:DirectoryPerMinute", "3"));
        var client = await factory.SignedInClientAsync();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/directory/users?q=abc")).StatusCode);

        var limited = await client.GetAsync("/api/directory/users?q=abc");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        // The limit is per user: someone else still has their own budget.
        var other = await factory.SignedInClientAsync("someone.else@example.com");
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/directory/users?q=abc")).StatusCode);
    }

    [Fact]
    public async Task DirectorySearchNeedsThreeCharacters()
    {
        using var factory = new RelayFactory(new Dictionary<string, string?> { ["Server:PublicUrl"] = T.Origin });
        var client = await factory.SignedInClientAsync();

        var two = await Users(client, "te");
        Assert.Empty(two.EnumerateArray());
        // Three characters searches; the signed-in user is in the stand-in directory.
        var three = await Users(client, "tester");
        Assert.Contains(three.EnumerateArray(), u => u.GetProperty("upn").GetString() == RelayFactory.DefaultUpn);
    }

    [Fact]
    public async Task PrinterSignOnIsLimited()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(
            ("Seed:ConnectionsJson", RelayFactory.SeedJson("test=tcp://127.0.0.1:1")),
            ("Printer:Port", fake.Port.ToString()),
            ("RateLimit:SignOnPerMinute", "2"));
        var client = await factory.SignedInClientAsync();

        // A wrong password still counts: after two attempts the next is throttled, not forwarded.
        for (var i = 0; i < 2; i++)
        {
            var attempt = await SignOn(client, "wrong");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, attempt.StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignOn(client, "wrong")).StatusCode);
    }

    private static Task<HttpResponseMessage> SignOn(HttpClient client, string password) =>
        client.PostAsJsonAsync("/api/printer/test/signon", new { user = "prtuser", password });

    private static async Task<JsonElement> Users(HttpClient client, string q)
    {
        var body = await (await client.GetAsync($"/api/directory/users?q={q}")).Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("users").Clone();
    }
}
