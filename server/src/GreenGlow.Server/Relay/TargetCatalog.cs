using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Relay;

/// <summary>The enabled connections in the database, in order; the first is the client's default.</summary>
public sealed class TargetCatalog(IDbContextFactory<GreenGlowDb> dbs)
{
    public async Task<IReadOnlyList<Target>> AllAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var rows = await db.Connections.AsNoTracking().Where(c => c.Enabled)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
        return rows.Select(ToTarget).ToList();
    }

    public async Task<Target?> FindAsync(string name)
    {
        if (!TargetParser.IsValidName(name)) return null;
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name && c.Enabled);
        return row is null ? null : ToTarget(row);
    }

    public static Target ToTarget(HostConnection c) =>
        new(c.Name, c.Label, c.Secure, c.Host, c.Port, c.MaxSessionsPerUser, c.IsTest, c.Color, c.TrustedCaPem, c.TrustedName);
}
