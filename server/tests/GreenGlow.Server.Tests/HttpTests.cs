using System.Net;
using System.Text.Json;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Tests;

public class HttpTests : IDisposable
{
    private readonly TestPki _pki = new();
    private readonly TempCaFile _ca;
    private readonly RelayFactory _factory;

    public HttpTests()
    {
        _ca = new TempCaFile(_pki.Ca);
        _factory = RelayFactory.For(
            "prod=tls://secret-host.example.com:9920,test=tcp://10.11.12.13:2323", "prod=Production,test=Test system",
            caFile: _ca.Path);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _ca.Dispose();
        _pki.Dispose();
    }

    [Fact]
    public async Task HealthzReturnsOk()
    {
        var response = await _factory.CreateClient().GetAsync("/healthz", T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TargetsListNamesLabelsAndSecureInOrder()
    {
        var response = await (await _factory.SignedInClientAsync()).GetAsync("/api/targets", T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var targets = json.RootElement.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(["prod", "test"], targets.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(["Production", "Test system"], targets.Select(t => t.GetProperty("label").GetString()));
        Assert.Equal([true, false], targets.Select(t => t.GetProperty("secure").GetBoolean()));
        // This server has a CA, so the tls:// one is verified; tcp:// never is.
        Assert.Equal([true, false], targets.Select(t => t.GetProperty("verified").GetBoolean()));
        Assert.All(targets, t => Assert.Equal(["name", "label", "secure", "verified", "color", "myColor"], t.EnumerateObject().Select(p => p.Name)));
        foreach (var leak in new[] { "secret-host", "10.11.12.13", "9920", "2323" })
            Assert.DoesNotContain(leak, body);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/targets")]
    [InlineData("/nothing-here")]
    public async Task EveryResponseCarriesSecurityHeaders(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path, T.Token());
        Assert.Equal(StaticSite.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
}
