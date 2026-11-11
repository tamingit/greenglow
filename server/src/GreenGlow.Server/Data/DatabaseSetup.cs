using GreenGlow.Server.Cluster;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Data;

public sealed class DataOptions
{
    /// <summary>The SQLite file. In the container it lives on the /app/data volume.</summary>
    public string DbPath { get; set; } = "data/greenglow.db";
}

public static class DatabaseSetup
{
    public static IServiceCollection AddGreenGlowDb(this IServiceCollection services, DataOptions options)
    {
        var path = Path.GetFullPath(options.DbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Every write to a synced row is versioned and journalled for the other nodes.
        services.AddDbContextFactory<GreenGlowDb>((provider, o) =>
            o.UseSqlite($"Data Source={path}").AddInterceptors(provider.GetRequiredService<SyncRecorder>()));
        return services;
    }

    /// <summary>
    /// Brings the schema up to date, closes log rows this node's previous run left open (its host
    /// sessions ended when it stopped; rows from before nodes have node 0), and moves sign-in keys
    /// an earlier version kept in files into the database.
    /// </summary>
    public static async Task PrepareAsync(IServiceProvider services, TimeProvider clock, DataOptions options)
    {
        var dbs = services.GetRequiredService<IDbContextFactory<GreenGlowDb>>();
        var node = services.GetRequiredService<ClusterNode>().Id;
        await using var db = await dbs.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await DbKeyRepository.ImportAsync(dbs, KeysFolder(options));
        await services.GetRequiredService<SyncStore>().JournalIdAsync();
        var open = await db.Logs.Where(l => l.EndedAt == null && (l.Node == node || l.Node == 0)).ToListAsync();
        foreach (var row in open)
        {
            row.EndedAt = clock.GetUtcNow();
            row.EndReason = "server restarted";
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Where sign-in keys were kept before they moved into the database.</summary>
    public static string KeysFolder(DataOptions options) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DbPath))!, "keys");
}
