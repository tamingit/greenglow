using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Auth;
using GreenGlow.Server.People;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>Who may use which connection: grants to people and groups; administrators see all.</summary>
public sealed class AccessTests
{
    private const string Admin = "boss@example.com";

    private static string Seed() => JsonSerializer.Serialize(new
    {
        connections = new object[]
        {
            new
            {
                name = "everyone", url = "tcp://127.0.0.1:1",
                access = new[] { new { type = "group", id = LocalDirectory.GroupId("Everyone"), name = "Everyone" } },
            },
            new
            {
                name = "developers", url = "tcp://127.0.0.1:1",
                access = new[] { new { type = "group", id = LocalDirectory.GroupId("Developers"), name = "Developers" } },
            },
            new
            {
                name = "pat-only", url = "tcp://127.0.0.1:1",
                access = new[] { new { type = "user", id = AuthSetup.DevObjectId("pat@example.com"), name = "pat@example.com" } },
            },
            new { name = "admins-only", url = "tcp://127.0.0.1:1", access = Array.Empty<object>() },
            new
            {
                name = "off", url = "tcp://127.0.0.1:1", enabled = false,
                access = new[] { new { type = "group", id = LocalDirectory.GroupId("Everyone"), name = "Everyone" } },
            },
        },
    });

    private static RelayFactory Factory(Action<IServiceCollection>? services = null) => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Entra:InitialAdminUpn"] = Admin,
        ["Dev:Groups"] = "Everyone=*,Developers=dev@example.com",
        ["Seed:ConnectionsJson"] = Seed(),
    })
    {
        ExtraServices = services,
    };

    private static async Task<List<string>> TargetsAsync(RelayFactory factory, string upn)
    {
        var client = await factory.SignedInClientAsync(upn);
        var body = JsonDocument.Parse(await client.GetStringAsync("/api/targets")).RootElement;
        return body.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    [Theory]
    [InlineData("someone@example.com", new[] { "everyone" })]
    [InlineData("dev@example.com", new[] { "everyone", "developers" })]
    [InlineData("pat@example.com", new[] { "everyone", "pat-only" })]
    [InlineData(Admin, new[] { "everyone", "developers", "pat-only", "admins-only" })]
    public async Task EachUserSeesTheConnectionsGrantedToThemOrTheirGroups(string upn, string[] expected)
    {
        using var factory = Factory();
        Assert.Equal(expected, await TargetsAsync(factory, upn));
    }

    [Theory]
    [InlineData("developers")]
    [InlineData("admins-only")]
    [InlineData("off")]
    [InlineData("nonexistent")]
    public async Task AConnectionNotGrantedAnswersLikeOneThatDoesNotExist(string target)
    {
        using var factory = Factory();
        var client = await factory.SignedInClientAsync("someone@example.com");
        var response = await client.PostAsJsonAsync("/api/sessions", new { target });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("unknown-target", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AGrantAddedByAnAdministratorAppliesAtOnce()
    {
        using var factory = Factory();
        Assert.DoesNotContain("admins-only", await TargetsAsync(factory, "sam@example.com"));
        var admin = await factory.SignedInClientAsync(Admin);
        var connections = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/connections")).RootElement;
        var id = connections.GetProperty("connections").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "admins-only").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/api/admin/connections/{id}/grants",
            new { type = "user", id = AuthSetup.DevObjectId("sam@example.com"), name = "sam@example.com" });
        Assert.Contains("admins-only", await TargetsAsync(factory, "sam@example.com"));
    }

    [Fact]
    public async Task WhenTheDirectoryCantBeReachedGroupAccessFailsClosed()
    {
        using var factory = Factory(s => s.AddSingleton<IDirectory, UnreachableDirectory>());
        Assert.Equal(["pat-only"], await TargetsAsync(factory, "pat@example.com"));
        Assert.Empty(await TargetsAsync(factory, "someone@example.com"));
        Assert.Equal(4, (await TargetsAsync(factory, Admin)).Count);
    }

    private sealed class UnreachableDirectory : IDirectory
    {
        public Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default) =>
            Task.FromResult<DirectoryEntry?>(new DirectoryEntry(AuthSetup.DevObjectId(upn), upn, upn));

        public Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default) =>
            throw new HttpRequestException("unreachable");

        public Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default) =>
            throw new HttpRequestException("unreachable");

        public Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default) =>
            throw new HttpRequestException("unreachable");

        public Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default) =>
            throw new HttpRequestException("unreachable");
    }
}
