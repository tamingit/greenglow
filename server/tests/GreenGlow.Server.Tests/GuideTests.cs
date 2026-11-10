using System.Net;
using System.Text;

namespace GreenGlow.Server.Tests;

/// <summary>The administrator guide is sent to administrators only, with its screenshots.</summary>
public sealed class GuideTests
{
    private const string Boss = "boss@example.com";

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
        ["Entra:InitialAdminUpn"] = Boss,
    };

    [Fact]
    public async Task AnAdministratorGetsTheGuideAndItsScreenshots()
    {
        using var factory = new RelayFactory(Settings());
        var client = await factory.SignedInClientAsync(Boss);

        var guide = await client.GetAsync("/api/admin/guide");
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        Assert.Equal("text/html", guide.Content.Headers.ContentType?.MediaType);
        Assert.Contains("private", guide.Headers.CacheControl?.ToString());
        var html = await guide.Content.ReadAsStringAsync();
        Assert.Contains("administrator guide", html);

        // Every screenshot the guide shows is there.
        var images = System.Text.RegularExpressions.Regex.Matches(html, "/api/admin/guide/images/([a-z0-9-]+\\.png)");
        Assert.NotEmpty(images);
        foreach (var image in images.Select(m => m.Value).Distinct())
        {
            var response = await client.GetAsync(image);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal("\u0089PNG", Encoding.Latin1.GetString(bytes, 0, 4));
        }
    }

    [Fact]
    public async Task NoOneElseGetsIt()
    {
        using var factory = new RelayFactory(Settings());
        var user = await factory.SignedInClientAsync("pat@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/guide")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/guide/images/admin-log.png")).StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/guide")).StatusCode);
    }

    [Theory]
    [InlineData("missing.png")]
    [InlineData("admin-guide.html")]
    [InlineData("..%2Fadmin-guide.html")]
    [InlineData("ADMIN-LOG.PNG")]
    public async Task OnlyTheGuidesOwnImagesAreServed(string name)
    {
        using var factory = new RelayFactory(Settings());
        var client = await factory.SignedInClientAsync(Boss);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/guide/images/{name}")).StatusCode);
    }
}
