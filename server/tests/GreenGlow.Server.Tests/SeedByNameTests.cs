using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>Seed access given by group name or UPN, looked up in the directory when a connection is added.</summary>
public sealed class SeedByNameTests
{
    private static RelayFactory Factory(string seed) => new(new Dictionary<string, string?>
    {
        ["Server:PublicUrl"] = T.Origin,
        ["Dev:Groups"] = "IBM i users=pat@example.com,Ops=sam@example.com",
        ["Seed:ConnectionsJson"] = seed,
    });

    [Fact]
    public async Task AGroupNameAndAUpnBecomeGrantsByObjectId()
    {
        using var factory = Factory("""
            {"connections":[{"name":"prod","url":"tls://ibmi.example","access":[
              {"type":"group","name":"IBM i users"},
              {"type":"user","name":"lee@example.com"}
            ]}]}
            """);
        _ = factory.Server;
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();
        var grants = (await db.Connections.Include(c => c.Grants).SingleAsync()).Grants.OrderBy(g => g.PrincipalType).ToList();
        Assert.Equal(
            [(PrincipalType.User, AuthSetup.DevObjectId("lee@example.com"), "lee@example.com"),
             (PrincipalType.Group, LocalDirectory.GroupId("IBM i users"), "IBM i users")],
            grants.Select(g => (g.PrincipalType, g.PrincipalId, g.DisplayName)));

        // The group's members may use it.
        var pat = await factory.SignedInClientAsync("pat@example.com");
        Assert.Contains("\"prod\"", await pat.GetStringAsync("/api/targets"));
    }

    [Fact]
    public async Task AGroupGivenByNameAndByIdIsOneGrant()
    {
        var id = LocalDirectory.GroupId("Ops");
        using var factory = Factory($$"""
            {"connections":[{"name":"prod","url":"tcp://h","access":[
              {"type":"group","name":"Ops"},{"type":"group","id":"{{id}}","name":"Ops"}]}]}
            """);
        _ = factory.Server;
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();
        Assert.Single((await db.Connections.Include(c => c.Grants).SingleAsync()).Grants);
    }

    [Theory]
    [InlineData("""{"type":"group","name":"No Such Group"}""", "no Entra ID group has that name")]
    [InlineData("""{"type":"user","name":"not-a-upn"}""", "no one in Entra ID has that UPN")]
    public void ANameThatCantBeFoundStopsTheStart(string access, string reason)
    {
        using var factory = Factory($$"""{"connections":[{"name":"prod","url":"tcp://h","access":[{{access}}]}]}""");
        var error = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains(reason, (error as ConfigException ?? error.InnerException)?.Message ?? error.Message);
    }

    [Fact]
    public void AccessNeedsAnIdOrAName() =>
        Assert.Contains("needs an id, or a name",
            Assert.Throws<ConfigException>(() => ConnectionSeed.Parse("seed",
                """{"connections":[{"name":"prod","url":"tcp://h","access":[{"type":"group"}]}]}""")).Message);

    [Fact]
    public async Task ANameTwoGroupsShareIsRefused()
    {
        using var factory = Factory("""{"connections":[]}""");
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();
        var seed = ConnectionSeed.Parse("seed", """{"connections":[{"name":"x","url":"tcp://h","access":[{"type":"group","name":"Twins"}]}]}""");
        var error = await Assert.ThrowsAsync<ConfigException>(() => ConnectionSeed.ApplyAsync(db, seed, new TwinGroups(), TimeProvider.System));
        Assert.Contains("2 Entra ID groups have that name", error.Message);
        Assert.Empty(await db.Connections.ToListAsync());
    }

    private sealed class TwinGroups : IDirectory
    {
        public Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyList<DirectoryEntry>>([new(Guid.NewGuid().ToString(), name, null), new(Guid.NewGuid().ToString(), name, null)]);

        public Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default) => throw new NotSupportedException();
    }
}
