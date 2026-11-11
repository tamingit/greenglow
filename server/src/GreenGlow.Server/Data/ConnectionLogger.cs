using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Data;

/// <summary>Writes the connection log: metadata only, never screen contents or keystrokes.</summary>
public sealed class ConnectionLogger(IDbContextFactory<GreenGlowDb> dbs, TimeProvider clock)
{
    public async Task<Guid> StartAsync(ConnectionLog entry)
    {
        entry.StartedAt = clock.GetUtcNow();
        return await AddAsync(entry);
    }

    /// <summary>Adds a row as it is, times included (an event that starts and ends at once).</summary>
    public async Task<Guid> AddAsync(ConnectionLog entry)
    {
        await using var db = await dbs.CreateDbContextAsync();
        db.Logs.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    /// <summary>Fills in what was learned during the session, such as the IBM i device.</summary>
    public async Task UpdateAsync(Guid id, Action<ConnectionLog> change)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Logs.FindAsync(id) is not { } row) return;
        change(row);
        await db.SaveChangesAsync();
    }

    public Task EndAsync(Guid id, string reason) => UpdateAsync(id, row =>
    {
        row.EndedAt ??= clock.GetUtcNow();
        row.EndReason ??= reason;
    });
}
