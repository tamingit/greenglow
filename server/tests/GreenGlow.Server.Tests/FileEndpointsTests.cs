using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Data;
using GreenGlow.Server.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>
/// /api/files: the Files panel's sign-on, path check and download. Without a file server to
/// reach, the access rules and refusals; with E2E_IBMI_* set, the whole download from a real
/// IBM i through the API (only system files are read; the password is never printed).
/// </summary>
public sealed class FileEndpointsTests
{
    private static RelayFactory Factory(string seed, params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = seed,
        };
        foreach (var (key, value) in extra) settings[key] = value;
        return new RelayFactory(settings);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string?> Error(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    private static async Task<List<ConnectionLog>> FileRowsAsync(RelayFactory factory)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        return await db.Logs.AsNoTracking().Where(r => r.Kind == LogKind.Files).OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task NothingIsReadBeforeSigningOnAndOtherSystemsDontExist()
    {
        using var factory = Factory(RelayFactory.SeedJson("test=tcp://127.0.0.1:1"));
        var client = await factory.SignedInClientAsync();

        var state = await Json(await client.GetAsync("/api/files/test"));
        Assert.False(state.GetProperty("signedOn").GetBoolean());
        Assert.Equal(JsonValueKind.Array, state.GetProperty("logins").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/files/nowhere")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/files/nowhere/download?path=/a")).StatusCode);
        foreach (var path in new[] { "/api/files/test/info?path=/a", "/api/files/test/download?path=/a" })
        {
            var refused = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("signon-needed", await Error(refused));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/files/test/signon")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/files/test")).StatusCode);
    }

    [Fact]
    public async Task AFileServerThatCantBeReachedIsSaidAndLogged()
    {
        // Nothing listens on the mapper's or the file server's port.
        using var factory = Factory(RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
            ("HostServers:MapperPort", "1"), ("HostServers:FilePort", "1"));
        var client = await factory.SignedInClientAsync();
        var refused = await client.PostAsJsonAsync("/api/files/test/signon", new { user = "prtuser", password = "Secret12" });
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("as-file", await refused.Content.ReadAsStringAsync());
        var row = Assert.Single(await FileRowsAsync(factory));
        Assert.Equal("PRTUSER", row.IbmUser);
        Assert.StartsWith("sign-on failed", row.EndReason);
    }

    [Theory]
    [InlineData("/home/me/report.csv", "report.csv")]
    [InlineData("/home/me/", "me")]
    [InlineData("/", "download")]
    public void TheDownloadIsNamedForThePathsLastPart(string path, string name) =>
        Assert.Equal(name, FileEndpoints.FileName(path));

    [Fact]
    public async Task ASystemFileDownloadsThroughTheApiFromARealIbmI()
    {
        var target = Environment.GetEnvironmentVariable("E2E_IBMI_TARGET");
        var user = Environment.GetEnvironmentVariable("E2E_IBMI_USER");
        var password = Environment.GetEnvironmentVariable("E2E_IBMI_PASSWORD");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("files API live test skipped: E2E_IBMI_* not set");
            return;
        }
        using var factory = Factory(RelayFactory.SeedJson($"live={target}"));
        var client = await factory.SignedInClientAsync();

        var signedOn = await Json(await client.PostAsJsonAsync("/api/files/live/signon", new { user, password }));
        Assert.Equal(user.Trim().ToUpperInvariant(), signedOn.GetProperty("ibmUser").GetString());

        const string path = "/QOpenSys/usr/bin/ls";
        var info = await Json(await client.GetAsync($"/api/files/live/info?path={Uri.EscapeDataString(path)}"));
        Assert.Equal("ls", info.GetProperty("name").GetString());
        var size = info.GetProperty("size").GetInt64();

        var download = await client.GetAsync($"/api/files/live/download?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(size, download.Content.Headers.ContentLength);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(size, bytes.Length);
        Assert.Equal([0x01, 0xF7], bytes[..2]);
        Console.WriteLine($"downloaded {bytes.Length} bytes through the API");

        var missing = await client.GetAsync("/api/files/live/info?path=/greenglow-no-such-folder/x");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var folder = await client.GetAsync("/api/files/live/info?path=/QOpenSys/usr/bin");
        Assert.Equal("folder", await Error(folder));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/files/live/signon")).StatusCode);
        var row = Assert.Single(await FileRowsAsync(factory));
        Assert.Equal(1, row.FilesOpened);
        Assert.Equal("signed off", row.EndReason);
    }
}
