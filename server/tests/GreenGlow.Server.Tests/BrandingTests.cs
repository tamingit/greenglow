using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Branding;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>Branding on the admin page: the name, colour and icon every page shows, and the public address.</summary>
public sealed class BrandingTests : IDisposable
{
    private const string Boss = "boss@example.com";

    private const string Page = """
        <!doctype html>
        <html lang="en">
          <head>
            <meta name="theme-color" content="#1d4d2c" />
            <link rel="icon" href="./icons/favicon-32.png" type="image/png" sizes="32x32" />
            <meta name="apple-mobile-web-app-title" content="GreenGlow" />
            <meta name="application-name" content="GreenGlow Connect" />
            <title>GreenGlow Connect administration</title>
          </head>
          <body><img class="bar-brand-mark" src="/icons/icon-192.png" alt="" /><h1>GreenGlow Connect</h1></body>
        </html>
        """;

    private const string Manifest = """
        {"name":"GreenGlow Connect","short_name":"GreenGlow","theme_color":"#1d4d2c","background_color":"#1d4d2c",
         "icons":[{"src":"icons/icon-192.png","sizes":"192x192"}],
         "shortcuts":[{"name":"Administration","url":"admin.html","icons":[{"src":"icons/icon-192.png"}]}]}
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("greenglow-brand-").FullName;

    public BrandingTests()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), Page);
        File.WriteAllText(Path.Combine(_dir, "admin.html"), Page);
        File.WriteAllText(Path.Combine(_dir, "manifest.webmanifest"), Manifest);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RelayFactory Factory() => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Server:StaticDir"] = _dir,
        ["Entra:InitialAdminUpn"] = Boss,
        ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
    });

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Just enough of a PNG for the server's check: the signature and an IHDR of this size.</summary>
    private static byte[] Png(int size)
    {
        var png = new byte[45];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)size);
        return png;
    }

    private static Dictionary<string, string> Icons(int? wrongSize = null) =>
        BrandIcons.Sizes.ToDictionary(p => p.Key, p => Convert.ToBase64String(Png(wrongSize ?? p.Value)));

    [Fact]
    public async Task OnlyAdministratorsChangeTheBrandOrTheAddress()
    {
        using var factory = Factory();
        var pat = await factory.SignedInClientAsync("pat@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await pat.GetAsync("/api/admin/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await pat.PutAsJsonAsync("/api/admin/branding", new { name = "Mine", shortName = "", color = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await pat.PutAsJsonAsync("/api/admin/branding/icon", new { icons = Icons() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await pat.PutAsJsonAsync("/api/admin/address", new { url = "https://evil.example.com" })).StatusCode);
    }

    [Fact]
    public async Task WithNoBrandThePagesAreServedAsBuilt()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        Assert.Equal(Page, await client.GetStringAsync("/"));
        Assert.Equal(Manifest, await client.GetStringAsync("/manifest.webmanifest"));
        Assert.DoesNotContain("--bar-bg", await client.GetStringAsync("/branding/brand.css"));
        var icon = await factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/branding/icon-192.png");
        Assert.Equal(HttpStatusCode.Redirect, icon.StatusCode);
        Assert.Equal("/icons/icon-192.png", icon.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ANameAndColourShowOnEveryPageAndInTheManifest()
    {
        using var factory = Factory();
        var boss = await factory.SignedInClientAsync(Boss);

        var saved = await Json(await boss.PutAsJsonAsync("/api/admin/branding",
            new { name = "Acme <Terminal>", shortName = "Acme", color = "#AA2233" }));
        Assert.Equal("Acme <Terminal>", saved.GetProperty("name").GetString());
        Assert.Equal("#aa2233", saved.GetProperty("color").GetString());
        var version = saved.GetProperty("version").GetString();

        // Anyone, signed in or not: the sign-in page is branded too.
        var anyone = factory.CreateClient();
        foreach (var path in new[] { "/", "/admin.html" })
        {
            var page = await anyone.GetStringAsync(path);
            Assert.DoesNotContain("GreenGlow", page);
            Assert.Contains("<title>Acme &lt;Terminal&gt; administration</title>", page);
            Assert.Contains("<meta name=\"apple-mobile-web-app-title\" content=\"Acme\" />", page);
            Assert.Contains("<meta name=\"theme-color\" content=\"#aa2233\" />", page);
            Assert.Contains($"<link rel=\"stylesheet\" href=\"branding/brand.css?v={version}\" />", page);
            // No custom icon: GreenGlow's.
            Assert.Contains("src=\"/icons/icon-192.png\"", page); // as in the sources Vite serves
        }
        var manifest = JsonDocument.Parse(await anyone.GetStringAsync("/manifest.webmanifest")).RootElement;
        Assert.Equal("Acme <Terminal>", manifest.GetProperty("name").GetString());
        Assert.Equal("Acme", manifest.GetProperty("short_name").GetString());
        Assert.Equal("#aa2233", manifest.GetProperty("theme_color").GetString());
        var css = await anyone.GetAsync("/branding/brand.css");
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--bar-bg: #aa2233;", await css.Content.ReadAsStringAsync());

        // Blank fields go back to GreenGlow's own, and the pages are as built again.
        await Json(await boss.PutAsJsonAsync("/api/admin/branding", new { name = "", shortName = (string?)null, color = " " }));
        Assert.Equal(Page, await anyone.GetStringAsync("/"));
    }

    [Theory]
    [InlineData("name", "", "red")]
    [InlineData("name", "", "#12345")]
    [InlineData("shortName", "A short name far too long to fit", "")]
    [InlineData("name", "Line\nbreak", "")]
    public async Task ABadNameOrColourIsRefused(string field, string text, string color)
    {
        using var factory = Factory();
        var boss = await factory.SignedInClientAsync(Boss);

        var response = await boss.PutAsJsonAsync("/api/admin/branding",
            field == "name" ? new { name = text, shortName = "", color } : new { name = "", shortName = text, color });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(color == "" ? field : "color", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ACustomIconReplacesEveryIconAndCanBeRemoved()
    {
        using var factory = Factory();
        var boss = await factory.SignedInClientAsync(Boss);

        var wrong = await boss.PutAsJsonAsync("/api/admin/branding/icon", new { icons = Icons(wrongSize: 64) });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var missing = Icons();
        missing.Remove("icon-512.png");
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.PutAsJsonAsync("/api/admin/branding/icon", new { icons = missing })).StatusCode);
        var notPng = Icons();
        notPng["favicon-32.png"] = Convert.ToBase64String("not a png at all, just some text"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.PutAsJsonAsync("/api/admin/branding/icon", new { icons = notPng })).StatusCode);

        var saved = await Json(await boss.PutAsJsonAsync("/api/admin/branding/icon", new { icons = Icons() }));
        Assert.True(saved.GetProperty("icon").GetBoolean());
        var version = saved.GetProperty("version").GetString();

        var anyone = factory.CreateClient();
        var icon = await anyone.GetAsync("/branding/icon-512.png");
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png(512), await icon.Content.ReadAsByteArrayAsync());
        var page = await anyone.GetStringAsync("/");
        Assert.Contains($"href=\"branding/favicon-32.png?v={version}\"", page);
        Assert.Contains($"src=\"branding/icon-192.png?v={version}\"", page);
        // Only the icon changed: the name and colour are GreenGlow's.
        Assert.Contains("<title>GreenGlow Connect administration</title>", page);
        Assert.DoesNotContain("brand.css", page);
        var manifest = JsonDocument.Parse(await anyone.GetStringAsync("/manifest.webmanifest")).RootElement;
        Assert.Equal($"branding/icon-192.png?v={version}", manifest.GetProperty("icons")[0].GetProperty("src").GetString());
        Assert.Equal($"branding/icon-192.png?v={version}",
            manifest.GetProperty("shortcuts")[0].GetProperty("icons")[0].GetProperty("src").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync("/branding/other.png")).StatusCode);

        Assert.False((await Json(await boss.DeleteAsync("/api/admin/branding/icon"))).GetProperty("icon").GetBoolean());
        Assert.Equal(Page, await anyone.GetStringAsync("/"));
    }

    [Fact]
    public async Task TheAddressCanBeChangedAndPutBack()
    {
        using var factory = Factory();
        var boss = await factory.SignedInClientAsync(Boss);
        const string moved = "https://terminal.example.com";

        var before = (await Json(await boss.GetAsync("/api/admin/branding"))).GetProperty("address");
        Assert.Equal(T.Origin, before.GetProperty("current").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("saved").ValueKind);

        var refused = await boss.PutAsJsonAsync("/api/admin/address", new { url = "https://terminal.example.com/?page=1" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var after = (await Json(await boss.PutAsJsonAsync("/api/admin/address", new { url = moved + "/" }))).GetProperty("address");
        Assert.Equal(moved, after.GetProperty("current").GetString());
        Assert.Equal(moved, after.GetProperty("saved").GetString());

        // The new address may now change things; the configured one still may.
        var origins = factory.Services.GetRequiredService<GreenGlow.Server.Web.OriginPolicy>();
        Assert.True(origins.IsAllowed(moved));
        Assert.True(origins.IsAllowed(T.Origin));

        var back = (await Json(await boss.PutAsJsonAsync("/api/admin/address", new { url = (string?)null }))).GetProperty("address");
        Assert.Equal(T.Origin, back.GetProperty("current").GetString());
        Assert.False(origins.IsAllowed(moved));

        var log = (await Json(await boss.GetAsync("/api/admin/log"))).GetRawText();
        Assert.Contains($"public address: {T.Origin} to {moved}", log);
    }

    [Theory]
    [InlineData("#1d4d2c", "#ffffff")]
    [InlineData("#f5e663", null)]
    public void TheBarsTextStaysReadableOnTheBrandColour(string color, string? expectedText)
    {
        var css = BrandColors.Css(color);

        var fg = System.Text.RegularExpressions.Regex.Match(css, "--bar-fg: (#[0-9a-f]{6});").Groups[1].Value;
        if (expectedText is not null) Assert.Equal(expectedText, fg);
        else Assert.NotEqual("#ffffff", fg);
        Assert.Contains($"--bar-bg: {color};", css);
        Assert.Contains(":root:root:root[data-look='night-shift']", css);
    }
}
