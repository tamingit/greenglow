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
        foreach (var path in new[] { "/api/files/test/info?path=/a", "/api/files/test/download?path=/a", "/api/files/test/list?path=/" })
        {
            var refused = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("signon-needed", await Error(refused));
        }
        var upload = await client.PutAsync("/api/files/test/upload?path=/tmp/a.bin", new ByteArrayContent([1, 2, 3]));
        Assert.Equal("signon-needed", await Error(upload));
        Assert.Equal("signon-needed", await Error(await client.PostAsync("/api/files/test/folder?path=/tmp/a", null)));
        Assert.Equal("signon-needed", await Error(await client.DeleteAsync("/api/files/test/entry?path=/tmp/a")));
        Assert.Equal("signon-needed", await Error(await client.PostAsync("/api/files/test/rename?path=/tmp/a&name=b", null)));
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsync("/api/files/nowhere/upload?path=/tmp/a.bin", new ByteArrayContent([1]))).StatusCode);
        // Without an allowed Origin, nothing is sent at all.
        var noOrigin = await factory.SignedInClientAsync();
        noOrigin.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noOrigin.PutAsync("/api/files/test/upload?path=/tmp/a.bin", new ByteArrayContent([1]))).StatusCode);
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
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/home/me/", "/home/me")]
    [InlineData("//home//me/./docs/../", "/home/me")]
    [InlineData("/../..", "/")]
    [InlineData("home/me", "home/me")]
    public void AFolderPathIsTidied(string path, string folder) => Assert.Equal(folder, FileEndpoints.FolderPath(path));

    [Theory]
    [InlineData("/", true)]
    [InlineData("/QSYS.LIB", true)]
    [InlineData("/home/", true)]
    [InlineData("/home/me", false)]
    [InlineData("/tmp/x/y", false)]
    public void TheRootAndTheFoldersInItAreNeverDeletedOrRenamed(string path, bool kept) =>
        Assert.Equal(kept, FileEndpoints.Protected(path));

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
        using var factory = Factory(RelayFactory.SeedJson($"live={target}"), ("Files:PageSize", "100"));
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

        // Pages of 100, each going on from where the last one's next said: every entry once.
        var entries = new List<JsonElement>();
        var url = "/api/files/live/list?path=/QOpenSys/usr/bin/";
        var pages = 0;
        while (true)
        {
            var listing = await Json(await client.GetAsync(url));
            Assert.Equal("/QOpenSys/usr/bin", listing.GetProperty("path").GetString());
            Assert.Equal(100, listing.GetProperty("pageSize").GetInt32());
            entries.AddRange(listing.GetProperty("entries").EnumerateArray());
            pages++;
            if (listing.GetProperty("next").ValueKind == JsonValueKind.Null) break;
            Assert.True(listing.GetProperty("truncated").GetBoolean());
            var next = listing.GetProperty("next");
            url = $"/api/files/live/list?path=/QOpenSys/usr/bin&afterId={next.GetProperty("id").GetString()}&afterName={Uri.EscapeDataString(next.GetProperty("name").GetString()!)}";
        }
        var names = entries.Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.True(pages > 1, "a few hundred entries make several pages of 100");
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "ls" && !e.GetProperty("folder").GetBoolean());
        Assert.Equal("after", await Error(await client.GetAsync("/api/files/live/list?path=/QOpenSys/usr/bin&afterId=zz&afterName=a")));
        var usr = (await Json(await client.GetAsync("/api/files/live/list?path=/QOpenSys/usr"))).GetProperty("entries").EnumerateArray().ToList();
        var bin = Assert.Single(usr, e => e.GetProperty("name").GetString() == "bin");
        Assert.True(bin.GetProperty("folder").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bin.GetProperty("size").ValueKind);
        // Folders come first.
        Assert.True(usr.TakeWhile(e => e.GetProperty("folder").GetBoolean()).Count() >= usr.Count(e => e.GetProperty("folder").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/files/live/list?path=/greenglow-no-such-folder")).StatusCode);

        var missing = await client.GetAsync("/api/files/live/info?path=/greenglow-no-such-folder/x");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var folder = await client.GetAsync("/api/files/live/info?path=/QOpenSys/usr/bin");
        Assert.Equal("folder", await Error(folder));

        // An upload makes the folders on the way; one already there is refused, then replaced.
        var root = "/tmp/greenglow-test-" + Guid.NewGuid().ToString("N")[..12];
        var uploaded = await client.PutAsync($"/api/files/live/upload?path={Uri.EscapeDataString(root + "/in/a.txt")}",
            new ByteArrayContent("placeholder"u8.ToArray()));
        Assert.Equal(11, (await Json(uploaded)).GetProperty("size").GetInt64());
        var again = await client.PutAsync($"/api/files/live/upload?path={Uri.EscapeDataString(root + "/in/a.txt")}",
            new ByteArrayContent("x"u8.ToArray()));
        Assert.Equal("exists", await Error(again));
        Assert.Equal(1, (await Json(await client.PutAsync(
            $"/api/files/live/upload?path={Uri.EscapeDataString(root + "/in/a.txt")}&replace=true",
            new ByteArrayContent("x"u8.ToArray())))).GetProperty("size").GetInt64());
        await Json(await client.PostAsync($"/api/files/live/folder?path={Uri.EscapeDataString(root + "/empty")}", null));
        var empty = await Json(await client.GetAsync($"/api/files/live/list?path={Uri.EscapeDataString(root + "/empty")}"));
        Assert.Empty(empty.GetProperty("entries").EnumerateArray());
        var inRoot = (await Json(await client.GetAsync($"/api/files/live/list?path={Uri.EscapeDataString(root)}")))
            .GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(["empty", "in"], inRoot);
        Assert.Equal("not-empty", await Error(await client.DeleteAsync($"/api/files/live/entry?folder=true&path={Uri.EscapeDataString(root)}")));
        // New folder refuses a name in use; rename refuses one too, then renames.
        Assert.Equal("exists", await Error(await client.PostAsync($"/api/files/live/folder?strict=true&path={Uri.EscapeDataString(root + "/empty")}", null)));
        Assert.Equal("exists", await Error(await client.PostAsync($"/api/files/live/rename?path={Uri.EscapeDataString(root + "/empty")}&name=in", null)));
        Assert.Equal(root + "/vacant", (await Json(await client.PostAsync(
            $"/api/files/live/rename?path={Uri.EscapeDataString(root + "/empty")}&name=vacant", null))).GetProperty("path").GetString());
        Assert.Equal("name", await Error(await client.PostAsync($"/api/files/live/rename?path={Uri.EscapeDataString(root + "/vacant")}&name=a/b", null)));
        // Never / or a folder directly in it.
        Assert.Equal("protected", await Error(await client.DeleteAsync("/api/files/live/entry?folder=true&recursive=true&path=/tmp")));
        Assert.Equal("protected", await Error(await client.PostAsync("/api/files/live/rename?path=/tmp&name=elsewhere", null)));
        // Everything in it, then it.
        Assert.Equal(4, (await Json(await client.DeleteAsync(
            $"/api/files/live/entry?folder=true&recursive=true&path={Uri.EscapeDataString(root)}"))).GetProperty("deleted").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/files/live/list?path={Uri.EscapeDataString(root)}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/files/live/signon")).StatusCode);
        var row = Assert.Single(await FileRowsAsync(factory));
        Assert.Equal(3, row.FilesOpened);
        Assert.Equal("signed off", row.EndReason);
    }
}
