using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Auth;
using GreenGlow.Server.People;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

public sealed class AdminTests
{
    private const string Boss = "boss@example.com";

    private static Dictionary<string, string?> Settings(string? initialAdmin = Boss, string? groups = null)
    {
        var s = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
        };
        if (initialAdmin is not null) s["Entra:InitialAdminUpn"] = initialAdmin;
        if (groups is not null) s["Dev:Groups"] = groups;
        return s;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Add(HttpClient client, string upn) =>
        client.PostAsJsonAsync("/api/admin/admins", new { upn });

    private static async Task<bool> IsAdmin(HttpClient client) =>
        (await Json(await client.GetAsync("/api/me"))).GetProperty("isAdmin").GetBoolean();

    private static async Task<JsonElement[]> Admins(HttpClient client) =>
        (await Json(await client.GetAsync("/api/admin/admins"))).GetProperty("admins").EnumerateArray().ToArray();

    [Fact]
    public async Task ARegularUserCannotUseTheAdminApi()
    {
        using var factory = new RelayFactory(Settings(initialAdmin: null));
        var client = await factory.SignedInClientAsync("pat@example.com");

        Assert.False(await IsAdmin(client));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/admins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Add(client, "sam@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/admin/admins/anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/directory/groups?q=a")).StatusCode);
    }

    [Theory]
    [InlineData("boss@example.com")]
    [InlineData("Boss@Example.com")]
    public async Task TheInitialAdministratorIsAnAdministratorWhateverTheCase(string upn)
    {
        using var factory = new RelayFactory(Settings());
        var client = await factory.SignedInClientAsync(upn);

        Assert.True(await IsAdmin(client));
        var admin = Assert.Single(await Admins(client));
        Assert.Equal(upn, admin.GetProperty("upn").GetString());
        Assert.True(admin.GetProperty("initial").GetBoolean());
    }

    [Fact]
    public async Task AnAdministratorCanAddSomeoneWhoHasNotSignedIn()
    {
        using var factory = new RelayFactory(Settings());
        var boss = await factory.SignedInClientAsync(Boss);

        var created = await Add(boss, "pat@example.com");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var pat = (await Admins(boss)).Single(a => a.GetProperty("upn").GetString() == "pat@example.com");
        Assert.Equal(JsonValueKind.Null, pat.GetProperty("lastSeenAt").ValueKind);
        Assert.False(pat.GetProperty("initial").GetBoolean());

        var patClient = await factory.SignedInClientAsync("pat@example.com");
        var me = await Json(await patClient.GetAsync("/api/me"));
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(pat.GetProperty("id").GetString(), me.GetProperty("id").GetString());
        Assert.Equal(AuthSetup.DevObjectId("pat@example.com"), me.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AddingNeedsAUpnThatTheDirectoryKnows()
    {
        using var factory = new RelayFactory(Settings());
        var boss = await factory.SignedInClientAsync(Boss);

        Assert.Equal(HttpStatusCode.BadRequest, (await Add(boss, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(boss, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Add(boss, "not-a-upn")).StatusCode);
    }

    [Fact]
    public async Task RemovingAnAdministratorAppliesAtOnce()
    {
        using var factory = new RelayFactory(Settings());
        var boss = await factory.SignedInClientAsync(Boss);
        await Add(boss, "pat@example.com");
        var pat = await factory.SignedInClientAsync("pat@example.com");
        Assert.True(await IsAdmin(pat));
        Assert.Equal(HttpStatusCode.OK, (await pat.GetAsync("/api/admin/admins")).StatusCode);

        var removed = await boss.DeleteAsync($"/api/admin/admins/{AuthSetup.DevObjectId("pat@example.com")}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        Assert.False(await IsAdmin(pat));
        Assert.Equal(HttpStatusCode.Forbidden, (await pat.GetAsync("/api/admin/admins")).StatusCode);
    }

    [Fact]
    public async Task TheLastAdministratorCannotBeRemoved()
    {
        using var factory = new RelayFactory(Settings());
        var boss = await factory.SignedInClientAsync(Boss);

        var response = await boss.DeleteAsync($"/api/admin/admins/{AuthSetup.DevObjectId(Boss)}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("last-admin",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        Assert.True(await IsAdmin(boss));
    }

    [Fact]
    public async Task RemovingSomeoneWhoIsNotAnAdministratorSucceeds()
    {
        using var factory = new RelayFactory(Settings());
        var boss = await factory.SignedInClientAsync(Boss);
        await factory.SignedInClientAsync("pat@example.com");

        Assert.Equal(HttpStatusCode.NoContent,
            (await boss.DeleteAsync($"/api/admin/admins/{AuthSetup.DevObjectId("pat@example.com")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await boss.DeleteAsync("/api/admin/admins/no-such-id")).StatusCode);
        Assert.Single(await Admins(boss));
    }

    [Fact]
    public async Task AUserKnownBeforeTheSettingExistedIsPromotedAtStart()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-test-").FullName;
        try
        {
            var first = Settings(initialAdmin: null);
            first["Data:DbPath"] = Path.Combine(dir, "greenglow.db");
            using (var before = new RelayFactory(first))
            {
                var client = await before.SignedInClientAsync(Boss);
                Assert.False(await IsAdmin(client));
            }

            var second = Settings();
            second["Data:DbPath"] = Path.Combine(dir, "greenglow.db");
            using var after = new RelayFactory(second);
            var again = await after.SignedInClientAsync(Boss);
            // No sign-in in this run: the promotion came from the start-up check alone.
            var user = await after.Services.GetRequiredService<UserStore>().FindAsync(AuthSetup.DevObjectId(Boss));
            Assert.True(user?.IsAdmin);
        }
        finally
        {
            T.ClearPool(Path.Combine(dir, "greenglow.db"));
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task APostFromAnotherSiteIsRefusedEvenForAnAdministrator()
    {
        using var factory = new RelayFactory(Settings());
        var cookie = await factory.SignInAsync(Boss);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);

        Assert.Equal(HttpStatusCode.Forbidden, (await Add(client, "pat@example.com")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await Add(client, "pat@example.com")).StatusCode);

        var boss = await factory.SignedInClientAsync(Boss);
        Assert.Single(await Admins(boss));
    }

    [Fact]
    public async Task TheDirectoryNeedsTwoCharactersAndOffersAnyUpn()
    {
        using var factory = new RelayFactory(Settings(initialAdmin: null));
        var client = await factory.SignedInClientAsync("pat@example.com");

        async Task<JsonElement[]> Users(string q) =>
            (await Json(await client.GetAsync($"/api/directory/users?q={Uri.EscapeDataString(q)}")))
                .GetProperty("users").EnumerateArray().ToArray();

        Assert.Empty(await Users("p"));
        Assert.Contains(await Users("pat"), u => u.GetProperty("upn").GetString() == "pat@example.com");
        Assert.Contains(await Users("new.person@example.com"), u => u.GetProperty("upn").GetString() == "new.person@example.com");
    }

    [Fact]
    public async Task TheStandInDirectoryKnowsTheGroupsInTheSettings()
    {
        using var factory = new RelayFactory(Settings(groups: "Developers=pat@example.com;sam@example.com,Ops=sam@example.com"));
        var boss = await factory.SignedInClientAsync(Boss);

        var groups = (await Json(await boss.GetAsync("/api/admin/directory/groups?q=dev"))).GetProperty("groups").EnumerateArray().ToArray();
        var group = Assert.Single(groups);
        Assert.Equal("Developers", group.GetProperty("name").GetString());
        Assert.Equal(LocalDirectory.GroupId("Developers"), group.GetProperty("id").GetString());

        await factory.SignInAsync("pat@example.com");
        await factory.SignInAsync("sam@example.com");
        var directory = factory.Services.GetRequiredService<IDirectory>();
        Assert.Equal(new HashSet<string> { LocalDirectory.GroupId("Developers") },
            await directory.MemberGroupsAsync(AuthSetup.DevObjectId("pat@example.com")));
        Assert.Equal(new HashSet<string> { LocalDirectory.GroupId("Developers"), LocalDirectory.GroupId("Ops") },
            await directory.MemberGroupsAsync(AuthSetup.DevObjectId("sam@example.com")));
        Assert.Empty(await directory.MemberGroupsAsync("unknown-id"));
    }
}
