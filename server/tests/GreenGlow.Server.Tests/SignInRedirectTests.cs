using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GreenGlow.Server.Tests;

/// <summary>
/// Behind a TLS proxy the server sees plain http, often with no X-Forwarded-Proto: the Entra ID
/// redirect must still go to the https public address.
/// </summary>
public sealed class SignInRedirectTests
{
    private static RelayFactory Factory(string publicUrl) => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = publicUrl,
        ["Entra:TenantId"] = "00000000-0000-0000-0000-000000000001",
        ["Entra:ClientId"] = "00000000-0000-0000-0000-000000000002",
        ["Entra:ClientSecret"] = "not-a-secret",
    })
    {
        // Entra's metadata, given here instead of fetched, so the test needs no network.
        ExtraServices = s => s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration
                {
                    Issuer = "https://login.example/tenant/v2.0",
                    AuthorizationEndpoint = "https://login.example/tenant/oauth2/v2.0/authorize",
                    TokenEndpoint = "https://login.example/tenant/oauth2/v2.0/token",
                })),
    };

    private static async Task<HttpResponseMessage> SignInAsync(RelayFactory factory, string host)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri($"http://{host}"),
        });
        return await client.GetAsync("/auth/signin?returnUrl=/");
    }

    private static string RedirectUri(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = response.Headers.Location!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        return query["redirect_uri"];
    }

    [Fact]
    public async Task BehindAProxyThatSendsNoForwardedHeadersTheRedirectIsHttps()
    {
        using var factory = Factory("https://greenglow.example.com");
        // The proxy forwarded plain http, under its own internal name.
        var response = await SignInAsync(factory, "127.0.0.1:5090");
        Assert.StartsWith("https://login.example/", response.Headers.Location!.ToString());
        Assert.Equal("https://greenglow.example.com/auth/callback", RedirectUri(response));
        // The cookies that carry sign-in across the round trip must be Secure, or browsers drop them.
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.NotEmpty(cookies);
        Assert.All(cookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithAnHttpPublicUrlTheRedirectFollowsTheBrowsersAddress()
    {
        using var factory = Factory("http://localhost:5080");
        // npm run dev: the browser is on Vite's port.
        var response = await SignInAsync(factory, "localhost:5173");
        Assert.Equal("http://localhost:5173/auth/callback", RedirectUri(response));
    }

    [Fact]
    public async Task TheHealthCheckStillAnswersWithAnHttpsPublicUrl()
    {
        using var factory = Factory("https://greenglow.example.com");
        var health = await factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
