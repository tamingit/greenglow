using GreenGlow.Server.Cluster;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GreenGlow.Server.Tests;

/// <summary>The move to UUID keys (ClusterSync) keeps every row and every link between rows.</summary>
public sealed class MigrationTests
{
    [Fact]
    public async Task IntegerKeysBecomeUuidsAndEveryLinkSurvives()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-migrate-").FullName;
        var path = Path.Combine(dir, "greenglow.db");
        var options = new DbContextOptionsBuilder<GreenGlowDb>().UseSqlite($"Data Source={path}").Options;
        const string session = "C0FFEE00-0000-4000-8000-000000000001";
        try
        {
            await using (var db = new GreenGlowDb(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261012144124_PrinterLog");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Users" ("Id", "Upn", "DisplayName", "IsAdmin", "DeviceStem", "FirstSeenAt", "LastSeenAt")
                        VALUES ('u1', 'pat@example.com', 'Pat', 1, 'GGABCDEF', 0, 0);
                    INSERT INTO "Connections" ("Id", "Name", "Label", "Host", "Port", "Secure", "MaxSessionsPerUser", "IsTest", "Enabled",
                        "SortOrder", "Color", "TrustedCaPem", "TrustedName", "TrustedBy", "TrustedAt", "CreatedAt", "UpdatedAt")
                        VALUES (7, 'test', 'Test', 'test.example.com', 23, 0, 3, 1, 1, 0, NULL, NULL, NULL, NULL, NULL, 0, 0),
                               (9, 'prod', 'Production', 'prod.example.com', 992, 1, 3, 0, 1, 1, '#123456', NULL, NULL, NULL, NULL, 0, 0);
                    INSERT INTO "Grants" ("Id", "ConnectionId", "PrincipalType", "PrincipalId", "DisplayName")
                        VALUES (3, 9, 'Group', 'g1', 'Ops'), (4, 7, 'User', 'u1', 'pat@example.com');
                    INSERT INTO "UserColors" ("UserId", "ConnectionId", "Color") VALUES ('u1', 9, '#112233');
                    INSERT INTO "Shares" ("Id", "SessionId", "OwnerUserId", "Token", "CreatedAt", "RevokedAt")
                        VALUES (5, 'C0FFEE00-0000-4000-8000-000000000001', 'u1', 'token', 0, NULL);
                    INSERT INTO "ShareRecipients" ("Id", "ShareId", "UserId", "Upn", "DisplayName", "Access")
                        VALUES (11, 5, 'u2', 'sam@example.com', 'Sam', 'FullAccess');
                    INSERT INTO "Macros" ("Id", "OwnerUserId", "OwnerUpn", "Name", "Description", "Content", "CreatedAt", "UpdatedAt")
                        VALUES (2, 'u1', 'pat@example.com', 'Orders', '', '<HAScript/>', 0, 0);
                    INSERT INTO "MacroShares" ("Id", "MacroId", "PrincipalType", "PrincipalId", "DisplayName") VALUES (4, 2, 'User', 'u2', 'sam');
                    INSERT INTO "MacroKeys" ("Id", "UserId", "MacroId", "Keys") VALUES (6, 'u2', 2, 'Alt+Shift+1');
                    INSERT INTO "Logs" ("Id", "Kind", "SessionId", "UserId", "Upn", "ClientIp", "ConnectionName", "DeviceName", "Role",
                        "IbmUser", "FilesOpened", "StartedAt", "EndedAt", "EndReason")
                        VALUES (1, 'Session', 'C0FFEE00-0000-4000-8000-000000000001', 'u1', 'pat@example.com', '10.0.0.1', 'prod', 'GGABCDEF1', NULL, NULL, NULL, 0, 0, 'ended');
                    """);
                await db.Database.MigrateAsync();
            }

            await using (var db = new GreenGlowDb(options))
            {
                var connections = await db.Connections.Include(c => c.Grants).OrderBy(c => c.SortOrder).ToListAsync();
                Assert.Equal(["test", "prod"], connections.Select(c => c.Name));
                Assert.NotEqual(connections[0].Id, connections[1].Id);
                Assert.Equal("pat@example.com", Assert.Single(connections[0].Grants).DisplayName);
                Assert.Equal("Ops", Assert.Single(connections[1].Grants).DisplayName);
                Assert.Equal(connections[1].Id, (await db.UserColors.SingleAsync()).ConnectionId);

                var share = await db.Shares.Include(s => s.Recipients).SingleAsync();
                Assert.Equal(Guid.Parse(session), share.SessionId);
                Assert.Equal("sam@example.com", Assert.Single(share.Recipients).Upn);

                var macro = await db.Macros.Include(m => m.Shares).SingleAsync();
                Assert.Equal("sam", Assert.Single(macro.Shares).DisplayName);
                Assert.Equal(macro.Id, (await db.MacroKeys.SingleAsync()).MacroId);

                var log = await db.Logs.SingleAsync();
                Assert.Equal((0, Hlc.Zero, "GGABCDEF1"), (log.Node, log.Version, log.DeviceName));
                Assert.Equal("pat@example.com", (await db.Users.SingleAsync()).Upn);

                Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT \"table\" AS \"Value\" FROM pragma_foreign_key_check").ToListAsync());

                // New rows go in beside the migrated ones, and foreign keys hold.
                db.Grants.Add(new ConnectionGrant { ConnectionId = connections[0].Id, PrincipalId = "g2", PrincipalType = PrincipalType.Group });
                await db.SaveChangesAsync();
                Assert.Equal(2, await db.Grants.CountAsync(g => g.ConnectionId == connections[0].Id));
            }
        }
        finally
        {
            T.ClearPool(path);
            Directory.Delete(dir, recursive: true);
        }
    }
}
