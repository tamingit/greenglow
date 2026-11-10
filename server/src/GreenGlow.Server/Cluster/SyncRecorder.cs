using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// Gives every write to a synced row a version and puts it in the change journal, in the same
/// transaction as the write, and stamps connection log rows with this node and a version. After
/// a save that journalled something, peers are poked. Rows applied from a peer are journalled by
/// the sync store itself, with the peer's version.
/// </summary>
public sealed class SyncRecorder(ClusterNode node, Hlc hlc, SyncSignals signals, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is GreenGlowDb { Applying: false } db)
        {
            signals.WriteGate.Wait();
            Hold(db);
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is GreenGlowDb { Applying: false } db)
        {
            await signals.WriteGate.WaitAsync(cancellationToken);
            Hold(db);
        }
        return result;
    }

    /// <summary>Records under the write gate; lets it go if recording fails, as no save follows.</summary>
    private void Hold(GreenGlowDb db)
    {
        db.HoldsWriteGate = true;
        try
        {
            Record(db);
        }
        catch
        {
            Release(db);
            throw;
        }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Release(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Release(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    private void Saved(DbContext? context)
    {
        Release(context);
        if (context is not GreenGlowDb { Journalled: true } db) return;
        db.Journalled = false;
        signals.LocalChange();
    }

    private void Release(DbContext? context)
    {
        if (context is not GreenGlowDb { HoldsWriteGate: true } db) return;
        db.HoldsWriteGate = false;
        signals.WriteGate.Release();
    }

    private void Record(GreenGlowDb db)
    {
        // SaveChanges detects changes after interceptors run; the journal needs them now.
        db.ChangeTracker.DetectChanges();
        var model = SyncModel.For(db.Model);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = new List<(SyncTable Table, EntityEntry Entry)>();
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is ConnectionLog log)
            {
                if (entry.State == EntityState.Deleted) continue;
                if (log.Node == 0) log.Node = node.Id;
                log.Version = hlc.Next();
                db.LogChanges.Add(new LogChange { LogId = log.Id, At = now });
                continue;
            }
            if (model.For(entry.Metadata.ClrType) is not { } table) continue;
            // Found missing, then inserted: a peer's copy may have arrived in between (this node
            // applies them under the same gate). Then this write updates it, newest winning.
            if (entry.State == EntityState.Added
                && SyncTable.Where(db, table.Entity, table.Key.Select(k => ((IReadOnlyProperty)k, SyncTable.ValueOf(entry.Entity, k))).ToList(),
                    tracked: false).Count > 0)
                entry.State = EntityState.Modified;
            changed.Add((table, entry));
        }
        if (changed.Count == 0) return;

        // Children's deletes before their parents', parents' writes before their children's, so a
        // peer applying the journal in order never sees a row before the row it hangs off.
        var ordered = changed.Where(c => c.Entry.State == EntityState.Deleted).OrderByDescending(c => c.Table.Rank)
            .Concat(changed.Where(c => c.Entry.State != EntityState.Deleted).OrderBy(c => c.Table.Rank));
        foreach (var (table, entry) in ordered)
        {
            var key = table.KeyOf(entry);
            var deleted = entry.State == EntityState.Deleted;
            var version = hlc.Next();
            var known = db.SyncVersions.Local.FirstOrDefault(v => v.Table == table.Name && v.Key == key)
                        ?? db.SyncVersions.FirstOrDefault(v => v.Table == table.Name && v.Key == key);
            if (known is null) db.SyncVersions.Add(new SyncVersion { Table = table.Name, Key = key, Version = version, Deleted = deleted });
            else (known.Version, known.Deleted) = (version, deleted);
            db.SyncChanges.Add(new SyncChange
            {
                Table = table.Name,
                Key = key,
                Version = version,
                Data = deleted ? null : table.Serialize(entry.Entity).ToJsonString(),
                At = now,
            });
        }
        db.Journalled = true;
    }
}

/// <summary>
/// Tells the sync service that this node wrote synced rows, so it pokes its peers; and the write
/// gate, which lets one write at a time on this node read and set row versions: a save of the
/// node's own, or a batch of a peer's rows being applied.
/// </summary>
public sealed class SyncSignals
{
    public SemaphoreSlim WriteGate { get; } = new(1, 1);

    public event Action? Changed;

    public void LocalChange() => Changed?.Invoke();
}
