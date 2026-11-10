using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>/api/macros: a user's own macros, and those shared with them.</summary>
public sealed class MacroTests
{
    private const string Pat = "pat@example.com";
    private const string Sam = "sam@example.com";

    private static async Task<JsonElement> ListAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/macros")).RootElement.GetProperty("macros").Clone();

    private static async Task<Guid> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/macros", new { name, description = "Placeholder" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("macro").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task AUserMakesRenamesAndDeletesTheirOwnMacros()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var pat = await factory.SignedInClientAsync(Pat);
        var id = await CreateAsync(pat, "Sign on");
        await CreateAsync(pat, "Another");

        // A new macro is an empty Host-on-Demand macro with its name.
        var one = JsonDocument.Parse(await pat.GetStringAsync($"/api/macros/{id}")).RootElement;
        Assert.Contains("<HAScript name=\"Sign on\"", one.GetProperty("content").GetString());
        Assert.Equal(["Another", "Sign on"], (await ListAsync(pat)).EnumerateArray().Select(m => m.GetProperty("name").GetString()));

        // Names are unique per user, ignoring case.
        Assert.Equal(HttpStatusCode.Conflict, (await pat.PostAsJsonAsync("/api/macros", new { name = "SIGN ON" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await pat.PutAsJsonAsync($"/api/macros/{id}", new { name = "another" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pat.PutAsJsonAsync($"/api/macros/{id}", new { name = "Sign on (test)" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await pat.DeleteAsync($"/api/macros/{id}")).StatusCode);
        Assert.Equal(["Another"], (await ListAsync(pat)).EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("", null, "name")]
    [InlineData("x", "<not a macro/>", "content")]
    [InlineData("x", "<HAScript>", "content")]
    [InlineData("x", "<!DOCTYPE HAScript [<!ENTITY e \"e\">]><HAScript>&e;</HAScript>", "content")]
    [InlineData("x", "<HAScript><screen><actions><input value=\"c2VjcmV0\" encrypted=\"true\" /></actions></screen></HAScript>", "password")]
    public async Task BadMacrosAreRefusedWithTheReason(string name, string? content, string error)
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var pat = await factory.SignedInClientAsync(Pat);
        var response = await pat.PostAsJsonAsync("/api/macros", new { name, content });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OthersSeeAMacroOnlyWhenSharedAndNeverChangeIt()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var pat = await factory.SignedInClientAsync(Pat);
        var sam = await factory.SignedInClientAsync(Sam);
        var mine = await CreateAsync(pat, "Mine");
        var forSam = await CreateAsync(pat, "For Sam");
        var forEveryone = await CreateAsync(pat, "For everyone");
        Assert.Empty((await ListAsync(sam)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync($"/api/macros/{mine}")).StatusCode);

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync())
        {
            db.MacroShares.Add(new MacroShare { MacroId = forSam, PrincipalType = PrincipalType.User, PrincipalId = AuthSetup.DevObjectId(Sam) });
            db.MacroShares.Add(new MacroShare { MacroId = forEveryone, PrincipalType = PrincipalType.Group, PrincipalId = RelayFactory.EveryoneGroup });
            await db.SaveChangesAsync();
        }
        var shared = await ListAsync(sam);
        Assert.Equal(["For everyone", "For Sam"], shared.EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.All(shared.EnumerateArray(), m => Assert.False(m.GetProperty("mine").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await sam.GetAsync($"/api/macros/{forSam}")).StatusCode);

        // Shared is play-only: only the owner changes or deletes it.
        Assert.Equal(HttpStatusCode.NotFound, (await sam.PutAsJsonAsync($"/api/macros/{forSam}", new { name = "Mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.DeleteAsync($"/api/macros/{forSam}")).StatusCode);
    }

    [Fact]
    public async Task TheOwnerSharesWithAPersonOrTheirOwnGroupAndTakesItBack()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var pat = await factory.SignedInClientAsync(Pat);
        var sam = await factory.SignedInClientAsync(Sam);
        var id = await CreateAsync(pat, "Month end");

        // A person, by UPN.
        var shared = await pat.PostAsJsonAsync($"/api/macros/{id}/shares", new { type = "user", upn = Sam });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var shareId = JsonDocument.Parse(await shared.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(["Month end"], (await ListAsync(sam)).EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal(1, (await ListAsync(pat))[0].GetProperty("sharedWith").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await pat.PostAsJsonAsync($"/api/macros/{id}/shares", new { type = "user", upn = Sam })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await pat.PostAsJsonAsync($"/api/macros/{id}/shares", new { type = "user", upn = Pat })).StatusCode);

        // A group Pat is in (everyone is in Everyone here); not one Pat isn't in.
        Assert.Equal(HttpStatusCode.Created, (await pat.PostAsJsonAsync($"/api/macros/{id}/shares",
            new { type = "group", groupId = RelayFactory.EveryoneGroup, name = "Everyone" })).StatusCode);
        var notMine = await pat.PostAsJsonAsync($"/api/macros/{id}/shares", new { type = "group", groupId = "not-a-group-of-pats", name = "Others" });
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);
        var shares = JsonDocument.Parse(await pat.GetStringAsync($"/api/macros/{id}/shares")).RootElement.GetProperty("shares");
        Assert.Equal([("user", Sam), ("group", "Everyone")],
            shares.EnumerateArray().Select(s => (s.GetProperty("type").GetString(), s.GetProperty("name").GetString())));

        // Only the owner sees or changes the shares.
        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync($"/api/macros/{id}/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.DeleteAsync($"/api/macros/{id}/shares/{shareId}")).StatusCode);

        // Taken back from Sam, Sam still sees it through Everyone; taken back from both, not at all.
        Assert.Equal(HttpStatusCode.NoContent, (await pat.DeleteAsync($"/api/macros/{id}/shares/{shareId}")).StatusCode);
        Assert.Single((await ListAsync(sam)).EnumerateArray());
        var group = shares.EnumerateArray().Single(s => s.GetProperty("type").GetString() == "group").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await pat.DeleteAsync($"/api/macros/{id}/shares/{group}")).StatusCode);
        Assert.Empty((await ListAsync(sam)).EnumerateArray());
    }

    [Fact]
    public async Task MyGroupsListsOnlyTheGroupsTheUserIsIn()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var pat = await factory.SignedInClientAsync(Pat);
        var groups = JsonDocument.Parse(await pat.GetStringAsync("/api/directory/my-groups?q=")).RootElement.GetProperty("groups");
        Assert.All(groups.EnumerateArray(), g => Assert.Equal(RelayFactory.EveryoneGroup, g.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task EachUserGivesMacrosTheirOwnKeyCombinations()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var pat = await factory.SignedInClientAsync(Pat);
        var sam = await factory.SignedInClientAsync(Sam);
        var first = await CreateAsync(pat, "First");
        var second = await CreateAsync(pat, "Second");
        Assert.Equal(HttpStatusCode.NoContent, (await pat.PutAsJsonAsync($"/api/macros/{first}/keys", new { keys = "Alt+Shift+1" })).StatusCode);
        Assert.Equal("Alt+Shift+1", (await ListAsync(pat)).EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == first).GetProperty("keys").GetString());

        // One macro per combination; only Alt+Shift and a letter or digit; not Alt+Shift+C.
        Assert.Equal(HttpStatusCode.Conflict, (await pat.PutAsJsonAsync($"/api/macros/{second}/keys", new { keys = "Alt+Shift+1" })).StatusCode);
        foreach (var bad in new[] { "Ctrl+1", "Alt+1", "Alt+Shift+C", "Alt+Shift+F1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await pat.PutAsJsonAsync($"/api/macros/{second}/keys", new { keys = bad })).StatusCode);

        // Someone it isn't shared with can't give it keys; once shared, they choose their own.
        Assert.Equal(HttpStatusCode.NotFound, (await sam.PutAsJsonAsync($"/api/macros/{first}/keys", new { keys = "Alt+Shift+2" })).StatusCode);
        await pat.PostAsJsonAsync($"/api/macros/{first}/shares", new { type = "user", upn = Sam });
        Assert.Equal(HttpStatusCode.NoContent, (await sam.PutAsJsonAsync($"/api/macros/{first}/keys", new { keys = "Alt+Shift+1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await pat.PutAsJsonAsync($"/api/macros/{first}/keys", new { keys = (string?)null })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ListAsync(pat)).EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == first).GetProperty("keys").ValueKind);
        Assert.Equal("Alt+Shift+1", (await ListAsync(sam))[0].GetProperty("keys").GetString());
    }
}
