using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

public sealed class DataTests
{
    private static GreenGlowDb Db(RelayFactory factory) =>
        factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();

    [Fact]
    public async Task SeededConnectionsAreAddedOnceAndTheDatabaseWins()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:2323,prod=tls://ibmi.example:992", "prod=Production");
        _ = factory.Server;
        await using (var db = Db(factory))
        {
            var rows = await db.Connections.OrderBy(c => c.SortOrder).ToListAsync();
            Assert.Equal(["test", "prod"], rows.Select(r => r.Name));
            Assert.Equal("Production", rows[1].Label);
            Assert.True(rows[1].Secure);
            rows[1].Label = "Changed by an administrator";
            await db.SaveChangesAsync();
        }

        await using (var db = Db(factory))
        {
            var seed = ConnectionSeed.Parse("test", RelayFactory.SeedJson("prod=tls://other:992,new=tcp://h"));
            Assert.Equal(["new"], await ConnectionSeed.ApplyAsync(db, seed, factory.Services.GetRequiredService<GreenGlow.Server.People.IDirectory>(), TimeProvider.System));
            var rows = await db.Connections.OrderBy(c => c.SortOrder).ToListAsync();
            Assert.Equal(["test", "prod", "new"], rows.Select(r => r.Name));
            Assert.Equal("Changed by an administrator", rows[1].Label);
            Assert.Equal("ibmi.example", rows[1].Host);
        }
    }

    [Fact]
    public async Task ASeedFileGivesLimitsTestFlagsAndAccess()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-seed-").FullName;
        var file = Path.Combine(dir, "connections.json");
        await File.WriteAllTextAsync(file, """
            {
              // Comments and trailing commas are fine.
              "connections": [
                {
                  "name": "dev", "label": "Development", "url": "tcp://127.0.0.1:23",
                  "maxSessionsPerUser": 5, "isTest": true,
                  "access": [
                    { "type": "group", "id": "11111111-1111-1111-1111-111111111111", "name": "IBM i users" },
                    { "type": "user", "id": "22222222-2222-2222-2222-222222222222", "name": "pat@example.com" },
                  ],
                },
                { "name": "off", "url": "tls://ibmi.example", "enabled": false },
              ],
            }
            """);
        try
        {
            using var factory = new RelayFactory(new Dictionary<string, string?>
            {
                ["Server:PublicUrl"] = T.Origin,
                ["Seed:ConnectionsFile"] = file,
            });
            _ = factory.Server;
            await using var db = Db(factory);
            var dev = await db.Connections.Include(c => c.Grants).SingleAsync(c => c.Name == "dev");
            Assert.Equal(("Development", 5, true, true), (dev.Label, dev.MaxSessionsPerUser, dev.IsTest, dev.Enabled));
            Assert.Equal(
                [(PrincipalType.Group, "11111111-1111-1111-1111-111111111111", "IBM i users"),
                 (PrincipalType.User, "22222222-2222-2222-2222-222222222222", "pat@example.com")],
                dev.Grants.OrderBy(g => g.PrincipalType == PrincipalType.User).Select(g => (g.PrincipalType, g.PrincipalId, g.DisplayName)));
            var off = await db.Connections.SingleAsync(c => c.Name == "off");
            Assert.Equal(("off", 992, true, false, false, 3), (off.Label, off.Port, off.Secure, off.Enabled, off.IsTest, off.MaxSessionsPerUser));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"connections":[{"name":"a","url":"tcp://h","maxSessionsPerUser":0}]}""", "maxSessionsPerUser")]
    [InlineData("""{"connections":[{"name":"a","url":"tcp://h","maxSessionsPerUser":10}]}""", "maxSessionsPerUser")]
    [InlineData("""{"connections":[{"name":"a","url":"http://h"}]}""", "scheme")]
    [InlineData("""{"connections":[{"name":"A","url":"tcp://h"}]}""", "name")]
    [InlineData("""{"connections":[{"name":"a","url":"tcp://h"},{"name":"a","url":"tcp://h"}]}""", "twice")]
    [InlineData("""{"connections":[{"name":"a","url":"tcp://h","access":[{"type":"role","id":"11111111-1111-1111-1111-111111111111"}]}]}""", "user or group")]
    [InlineData("""{"connections":[{"name":"a","url":"tcp://h","access":[{"type":"user","id":"pat@example.com"}]}]}""", "object ID")]
    [InlineData("""{"connections":[{"name":"a","url":"tcp://h","hostname":"x"}]}""", "not valid seed JSON")]
    [InlineData("not json", "not valid seed JSON")]
    public void BadSeedsAreRejectedWithTheReason(string json, string reason)
    {
        var error = Assert.Throws<ConfigException>(() => ConnectionSeed.Parse("seed", json));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void ABadSeedStopsTheServerFromStarting()
    {
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = """{"connections":[{"name":"a","url":"ftp://h"}]}""",
        });
        var error = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("tls:// or tcp://", (error as ConfigException ?? error.InnerException)?.Message ?? error.Message);
    }

    [Fact]
    public async Task DisabledConnectionsAreNotOffered()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1,other=tcp://h");
        _ = factory.Server;
        await using (var db = Db(factory))
        {
            (await db.Connections.SingleAsync(c => c.Name == "other")).Enabled = false;
            await db.SaveChangesAsync();
        }
        var body = await (await factory.SignedInClientAsync()).GetStringAsync("/api/targets");
        Assert.Contains("\"test\"", body);
        Assert.DoesNotContain("\"other\"", body);
        var response = await (await factory.SignedInClientAsync()).PostAsync("/api/sessions",
            new StringContent("""{"target":"other"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SettingsUseConfiguredDefaultsUntilAnAdministratorSavesOne()
    {
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Sessions:MaxFrontends"] = "4",
            ["Sessions:IdleMinutes"] = "5",
        });
        var settings = factory.Services.GetRequiredService<SettingsStore>();
        Assert.Equal(4, settings.MaxFrontendsPerSession);
        Assert.Equal(5, settings.IdleMinutes);
        Assert.Equal(50, settings.MaxConnections);

        await settings.SetAsync(SettingKeys.IdleMinutes, 12);
        Assert.Equal(12, settings.IdleMinutes);
        Assert.Contains(settings.All(), s => s is { Key: SettingKeys.IdleMinutes, Value: 12, IsDefault: false });
        await settings.SetAsync(SettingKeys.IdleMinutes, null);
        Assert.Equal(5, settings.IdleMinutes);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => settings.SetAsync(SettingKeys.MaxFrontendsPerSession, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetAsync("Nope", 1));
    }

    [Fact]
    public async Task RowsLeftOpenByAPreviousRunAreClosedAtStart()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-test-").FullName;
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Data:DbPath"] = Path.Combine(dir, "greenglow.db"),
        };
        using (var first = new RelayFactory(settings))
        {
            await using var db = Db(first);
            db.Logs.Add(new ConnectionLog { Kind = LogKind.Session, ConnectionName = "test", StartedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using (var second = new RelayFactory(settings))
        {
            await using var db = Db(second);
            var row = await db.Logs.SingleAsync();
            Assert.Equal("server restarted", row.EndReason);
        }
        T.ClearPool(Path.Combine(dir, "greenglow.db"));
        Directory.Delete(dir, recursive: true);
    }
}
