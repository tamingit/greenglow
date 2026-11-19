using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GreenGlow.Server.Tests;

/// <summary>
/// The app under a base path: Server__PublicUrl (or the address an administrator saves) with a
/// path, such as http://localhost:5080/greenglow, behind a proxy that serves other things at the root.
/// </summary>
public sealed class BasePathTests : IDisposable
{
    private const string Boss = "boss@example.com";
    private const string Base = T.Origin + "/greenglow";

    private readonly string _dir = Directory.CreateTempSubdirectory("greenglow-base-").FullName;

    public BasePathTests() => File.WriteAllText(Path.Combine(_dir, "index.html"), "<!doctype html><title>the app</title>");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RelayFactory Factory(string publicUrl = Base, bool trustForwarded = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = publicUrl,
            ["Server:StaticDir"] = _dir,
            ["Entra:InitialAdminUpn"] = Boss,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
        };
        if (trustForwarded) settings["Server:TrustForwardedHeaders"] = "true";
        return new RelayFactory(settings);
    }

    private static HttpClient Anonymous(RelayFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("http://localhost:5080", "http://localhost:5080")]
    [InlineData("http://localhost:5080/", "http://localhost:5080")]
    [InlineData("https://example.com/greenglow/", "https://example.com/greenglow")]
    [InlineData("https://example.com/apps/green-glow", "https://example.com/apps/green-glow")]
    [InlineData("https://example.com/greenglow?x=1", null)]
    [InlineData("https://example.com/greenglow#top", null)]
    [InlineData("https://example.com/a%20b", null)]
    [InlineData("https://example.com/a//b", null)]
    // The root is served too: a base path named like the app's own paths would hide them.
    [InlineData("https://example.com/api", null)]
    [InlineData("https://example.com/WS/x", null)]
    [InlineData("https://example.com/index.html", null)]
    [InlineData("ftp://example.com/greenglow", null)]
    public void AnAddressMayCarryABasePath(string address, string? kept)
    {
        Assert.Equal(kept, PublicAddress.Parse(address));
        if (kept is null) return;
        Assert.Equal(new Uri(address).GetLeftPart(UriPartial.Authority), PublicAddress.Origin(kept));
    }

    [Fact]
    public async Task TheAppAnswersUnderItsBasePathAndStillAtTheRoot()
    {
        using var factory = Factory();
        var client = await factory.SignedInClientAsync(Boss);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/greenglow/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Contains("the app", await client.GetStringAsync("/greenglow/"));
        Assert.Contains("the app", await client.GetStringAsync("/greenglow/index.html"));
        // A request that changes something, from the public URL's origin.
        var limit = await client.PutAsJsonAsync("/greenglow/api/admin/settings/Sessions.IdleMinutes", new { value = 7 });
        Assert.Equal(HttpStatusCode.NoContent, limit.StatusCode);

        // Not the same path with more letters.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/greenglowing/api/me")).StatusCode);
    }

    [Fact]
    public async Task TheBasePathAloneGoesToTheFirstPage()
    {
        using var factory = Factory();
        var response = await Anonymous(factory).GetAsync("/greenglow?share=abc");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // The pages' addresses are relative to it, so it must end in a slash.
        Assert.Equal("/greenglow/?share=abc", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SignInKeepsTheBasePath()
    {
        using var factory = Factory();
        var anonymous = Anonymous(factory);

        var asked = await anonymous.GetAsync("/greenglow/auth/signin?returnUrl=/greenglow/admin.html");
        Assert.Equal("/greenglow/auth/dev?returnUrl=%2Fgreenglow%2Fadmin.html", asked.Headers.Location?.OriginalString);
        var page = await anonymous.GetStringAsync("/greenglow/auth/dev");
        Assert.Contains("action=\"/greenglow/auth/dev\"", page);
        Assert.Contains("value=\"/greenglow/\"", page);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/greenglow/auth/dev")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["upn"] = "pat@example.com", ["returnUrl"] = "" }),
        };
        request.Headers.Add("Origin", T.Origin);
        var signedIn = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/greenglow/", signedIn.Headers.Location?.OriginalString);

        // Signing out goes back to the base path too.
        using var signOut = new HttpRequestMessage(HttpMethod.Post, "/greenglow/auth/signout");
        signOut.Headers.Add("Origin", T.Origin);
        signOut.Headers.Add("Cookie", await factory.SignInAsync("pat@example.com"));
        Assert.Equal("/greenglow/", (await anonymous.SendAsync(signOut)).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AProxyThatStripsThePathSaysWhatItWas()
    {
        using var factory = Factory(trustForwarded: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/signin?returnUrl=/greenglow/");
        request.Headers.Add("X-Forwarded-Prefix", "/greenglow");

        var response = await Anonymous(factory).SendAsync(request);
        Assert.Equal("/greenglow/auth/dev?returnUrl=%2Fgreenglow%2F", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AnAdministratorMovesTheAppUnderAPathAndBack()
    {
        using var factory = Factory(publicUrl: T.Origin);
        var boss = await factory.SignedInClientAsync(Boss);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await boss.PutAsJsonAsync("/api/admin/address", new { url = T.Origin + "/api" })).StatusCode);

        var moved = await boss.PutAsJsonAsync("/api/admin/address", new { url = T.Origin + "/terminal/" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var address = JsonDocument.Parse(await moved.Content.ReadAsStringAsync()).RootElement.GetProperty("address");
        Assert.Equal(T.Origin + "/terminal", address.GetProperty("current").GetString());

        // At once, with no restart: the new path, and the root as well.
        Assert.Equal(HttpStatusCode.OK, (await boss.GetAsync("/terminal/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await boss.GetAsync("/api/me")).StatusCode);
        Assert.Equal("/terminal/auth/dev?returnUrl=%2Fterminal%2F",
            (await Anonymous(factory).GetAsync("/terminal/auth/signin")).Headers.Location?.OriginalString);

        await boss.PutAsJsonAsync("/api/admin/address", new { url = (string?)null });
        Assert.Equal(HttpStatusCode.NotFound, (await boss.GetAsync("/terminal/api/me")).StatusCode);
    }
}
