using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.Tests;

/// <summary>A connection's colour: the administrator's, and each user's own in its place.</summary>
public sealed class ConnectionColorTests
{
    private const string Admin = "boss@example.com";

    private static RelayFactory Factory() => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Entra:InitialAdminUpn"] = Admin,
        ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:2323", "test=Test system"),
    });

    private static async Task<JsonElement> TargetAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/targets")).RootElement
            .GetProperty("targets").EnumerateArray().Single();

    private static string? Text(JsonElement target, string property) => target.GetProperty(property).GetString();

    private static async Task<Guid> IdAsync(HttpClient admin) =>
        JsonDocument.Parse(await admin.GetStringAsync("/api/admin/connections")).RootElement
            .GetProperty("connections").EnumerateArray().Single().GetProperty("id").GetGuid();

    [Fact]
    public async Task AnAdministratorSetsAndClearsAConnectionsColour()
    {
        using var factory = Factory();
        var admin = await factory.SignedInClientAsync(Admin);
        var user = await factory.SignedInClientAsync("pat@example.com");
        var id = await IdAsync(admin);
        Assert.Null(Text(await TargetAsync(user), "color"));

        var set = await admin.PutAsJsonAsync($"/api/admin/connections/{id}", new { color = "#C98A1B" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("#c98a1b", Text(await TargetAsync(user), "color"));

        var bad = await admin.PutAsJsonAsync($"/api/admin/connections/{id}", new { color = "orange" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("color", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        // Other edits leave it alone; an empty colour takes it away.
        await admin.PutAsJsonAsync($"/api/admin/connections/{id}", new { label = "Test" });
        Assert.Equal("#c98a1b", Text(await TargetAsync(user), "color"));
        await admin.PutAsJsonAsync($"/api/admin/connections/{id}", new { color = "" });
        Assert.Null(Text(await TargetAsync(user), "color"));
    }

    [Fact]
    public async Task AUsersOwnColourIsTheirsAlone()
    {
        using var factory = Factory();
        var admin = await factory.SignedInClientAsync(Admin);
        await admin.PutAsJsonAsync($"/api/admin/connections/{await IdAsync(admin)}", new { color = "#c98a1b" });
        var pat = await factory.SignedInClientAsync("pat@example.com");
        var sam = await factory.SignedInClientAsync("sam@example.com");

        Assert.Equal(HttpStatusCode.NoContent,
            (await pat.PutAsJsonAsync("/api/targets/test/color", new { color = "#3366FF" })).StatusCode);
        var mine = await TargetAsync(pat);
        Assert.Equal(("#c98a1b", "#3366ff"), (Text(mine, "color"), Text(mine, "myColor")));
        Assert.Null(Text(await TargetAsync(sam), "myColor"));

        // Changed, then back to the system's colour.
        await pat.PutAsJsonAsync("/api/targets/test/color", new { color = "#112233" });
        Assert.Equal("#112233", Text(await TargetAsync(pat), "myColor"));
        Assert.Equal(HttpStatusCode.NoContent,
            (await pat.PutAsJsonAsync("/api/targets/test/color", new { color = (string?)null })).StatusCode);
        Assert.Null(Text(await TargetAsync(pat), "myColor"));
    }

    [Fact]
    public async Task OnlyAColourForASystemTheUserMayUseIsKept()
    {
        using var factory = Factory();
        var pat = await factory.SignedInClientAsync("pat@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await pat.PutAsJsonAsync("/api/targets/elsewhere/color", new { color = "#112233" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await pat.PutAsJsonAsync("/api/targets/test/color", new { color = "#12345" })).StatusCode);
    }

    [Theory]
    [InlineData("#a1B2c3", "#a1b2c3")]
    [InlineData(" #000000 ", "#000000")]
    [InlineData("a1b2c3", null)]
    [InlineData("#abc", null)]
    [InlineData("#gggggg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ColoursAreSixDigitHex(string? text, string? expected) => Assert.Equal(expected, SystemColor.Parse(text));

    [Fact]
    public void TheSeedCanGiveAColourAndRefusesABadOne()
    {
        var seeded = ConnectionSeed.Parse("seed", """{"connections":[{"name":"t","url":"tcp://h","color":"#C98A1B"}]}""");
        Assert.Equal("#c98a1b", seeded.Single().Color);
        Assert.Throws<ConfigException>(() =>
            ConnectionSeed.Parse("seed", """{"connections":[{"name":"t","url":"tcp://h","color":"amber"}]}"""));
    }
}
