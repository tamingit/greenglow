using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GreenGlow.Server.Tests;

/// <summary>/api/admin/connections and /api/admin/settings.</summary>
public sealed class ConnectionAdminTests
{
    private const string Admin = "boss@example.com";

    private static RelayFactory Factory() => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Entra:InitialAdminUpn"] = Admin,
        ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:2323", "test=Test system"),
    });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> ConnectionAsync(HttpClient admin, string name) =>
        (await Json(await admin.GetAsync("/api/admin/connections"))).GetProperty("connections").EnumerateArray()
        .Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public async Task OnlyAdministratorsManageConnections()
    {
        using var factory = Factory();
        var user = await factory.SignedInClientAsync("pat@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/admin/connections", new { name = "x", url = "tcp://h" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/settings")).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorAddsEditsAndDeletesAConnection()
    {
        using var factory = Factory();
        var admin = await factory.SignedInClientAsync(Admin);
        var seeded = await ConnectionAsync(admin, "test");
        Assert.Equal("tcp://127.0.0.1:2323", seeded.GetProperty("url").GetString());

        var created = await admin.PostAsJsonAsync("/api/admin/connections",
            new { name = "prod", label = "Production", url = "tls://ibmi.example.com", maxSessionsPerUser = 2 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var prod = await Json(created);
        Assert.Equal(("tls://ibmi.example.com:992", true, 2, false), (prod.GetProperty("url").GetString(),
            prod.GetProperty("secure").GetBoolean(), prod.GetProperty("maxSessionsPerUser").GetInt32(), prod.GetProperty("isTest").GetBoolean()));
        var id = prod.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/admin/connections", new { name = "prod", url = "tcp://h" })).StatusCode);

        var edited = await admin.PutAsJsonAsync($"/api/admin/connections/{id}",
            new { label = "Production (TLS)", maxSessionsPerUser = 4, enabled = false });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var again = await ConnectionAsync(admin, "prod");
        Assert.Equal(("Production (TLS)", 4, false), (again.GetProperty("label").GetString(),
            again.GetProperty("maxSessionsPerUser").GetInt32(), again.GetProperty("enabled").GetBoolean()));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/admin/connections/{id}", new { name = "renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/connections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/connections/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"Bad Name","url":"tcp://h"}""", "name")]
    [InlineData("""{"name":"ok","url":"http://h"}""", "url")]
    [InlineData("""{"name":"ok","url":"tcp://h/path"}""", "url")]
    [InlineData("""{"name":"ok","url":"tcp://h","maxSessionsPerUser":0}""", "maxSessionsPerUser")]
    [InlineData("""{"name":"ok","url":"tcp://h","maxSessionsPerUser":10}""", "maxSessionsPerUser")]
    [InlineData("""{"name":"ok","url":"tcp://h","label":"0123456789012345678901234567890123456789x"}""", "label")]
    public async Task BadConnectionsAreRefusedWithTheField(string body, string field)
    {
        using var factory = Factory();
        var admin = await factory.SignedInClientAsync(Admin);
        var response = await admin.PostAsync("/api/admin/connections",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GrantsAreAddedOnceAndRemoved()
    {
        using var factory = Factory();
        var admin = await factory.SignedInClientAsync(Admin);
        var id = (await ConnectionAsync(admin, "test")).GetProperty("id").GetGuid();
        var group = Guid.NewGuid().ToString();
        var first = await admin.PostAsJsonAsync($"/api/admin/connections/{id}/grants", new { type = "group", id = group, name = "Ops" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var again = await admin.PostAsJsonAsync($"/api/admin/connections/{id}/grants", new { type = "group", id = group, name = "Ops" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/admin/connections/{id}/grants", new { type = "role", id = group })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/admin/connections/{id}/grants", new { type = "user", id = "pat@example.com" })).StatusCode);

        var grants = (await ConnectionAsync(admin, "test")).GetProperty("grants").EnumerateArray().ToList();
        var ops = grants.Single(g => g.GetProperty("principalId").GetString() == group);
        Assert.Equal("Ops", ops.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/admin/connections/{id}/grants/{ops.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.DoesNotContain((await ConnectionAsync(admin, "test")).GetProperty("grants").EnumerateArray(),
            g => g.GetProperty("principalId").GetString() == group);
    }

    [Fact]
    public async Task SettingsAreSavedAndResetToTheDefault()
    {
        using var factory = Factory();
        var admin = await factory.SignedInClientAsync(Admin);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync("/api/admin/settings/Sessions.MaxFrontends", new { value = 4 })).StatusCode);
        var settings = (await Json(await admin.GetAsync("/api/admin/settings"))).GetProperty("settings").EnumerateArray().ToList();
        var frontends = settings.Single(s => s.GetProperty("key").GetString() == "Sessions.MaxFrontends");
        Assert.Equal((4, false), (frontends.GetProperty("value").GetInt32(), frontends.GetProperty("isDefault").GetBoolean()));
        Assert.Equal(4, (await Json(await admin.GetAsync("/api/me"))).GetProperty("maxFrontends").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/admin/settings/Sessions.MaxFrontends", new { value = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync("/api/admin/settings/Nope", new { value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync("/api/admin/settings/Sessions.MaxFrontends", new { value = (int?)null })).StatusCode);
        Assert.Equal(2, (await Json(await admin.GetAsync("/api/me"))).GetProperty("maxFrontends").GetInt32());
    }
}
