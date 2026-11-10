using System.Net;

namespace GreenGlow.Server.Tests;

/// <summary>The built web app: the pages, and what installing it as an app needs.</summary>
public sealed class StaticTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("greenglow-static-").FullName;

    public StaticTests()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<!doctype html><title>t</title>");
        File.WriteAllText(Path.Combine(_dir, "manifest.webmanifest"), """{"name":"t"}""");
        Directory.CreateDirectory(Path.Combine(_dir, "icons"));
        File.WriteAllBytes(Path.Combine(_dir, "icons", "icon-192.png"), [0x89, 0x50, 0x4e, 0x47]);
        File.WriteAllText(Path.Combine(_dir, "icons", "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RelayFactory Factory() => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Server:StaticDir"] = _dir,
    });

    [Theory]
    [InlineData("/manifest.webmanifest", "application/manifest+json")]
    [InlineData("/icons/icon-192.png", "image/png")]
    [InlineData("/icons/icon.svg", "image/svg+xml")]
    [InlineData("/", "text/html")]
    public async Task WhatInstallingNeedsIsServedWithoutSignInAndRevalidated(string path, string type)
    {
        using var factory = Factory();
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }
}
