using System.Text.Json;
using System.Text.Json.Nodes;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Cluster;

/// <summary>One synced row as it travels between nodes: null data is a delete.</summary>
public sealed record Change(string Table, string Key, string Version, string? Data);

/// <summary>Journal entries after a peer's cursor, or Resync when the cursor can't be served.</summary>
public sealed record ChangesPage(string JournalId, long Head, bool Resync, IReadOnlyList<JournalEntry> Entries);

public sealed record JournalEntry(long Seq, string Table, string Key, string Version, string? Data)
{
    public Change ToChange() => new(Table, Key, Version, Data);
}

/// <summary>Every synced row and tombstone a node has, and where its journal stood then.</summary>
public sealed record Snapshot(string JournalId, long Head, IReadOnlyList<Change> Rows);

/// <summary>Connection log rows changed after a cursor.</summary>
public sealed record LogsPage(string JournalId, long Next, bool More, IReadOnlyList<JsonObject> Rows);

/// <summary>
/// The node's side of sync over its own database: the journal and snapshots peers read, and
/// applying what peers send. The newest version of a row wins; when a unique rule allows only one
/// of two rows, the newer stays and the other goes with the rows that hang off it, the same way
/// on every node.
/// </summary>
public sealed class SyncStore(IDbContextFactory<GreenGlowDb> dbs, Hlc hlc, SyncSignals signals, TimeProvider clock,
    ILogger<SyncStore> log)
{
    public const string JournalIdKey = "JournalId";

    /// <summary>How long journal entries are kept; a peer further behind takes a snapshot.</summary>
    public static readonly TimeSpan JournalKept = TimeSpan.FromDays(30);

    /// <summary>Raised after rows from a peer were applied, with their tables, so caches can forget them.</summary>
    public event Action<IReadOnlyCollection<string>>? Applied;

    /// <summary>This database's journal id, made the first time it is asked for.</summary>
    public async Task<string> JournalIdAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await JournalIdAsync(db);
    }

    private static async Task<string> JournalIdAsync(GreenGlowDb db)
    {
        if (await db.LocalState.FindAsync(JournalIdKey) is { } row) return row.Value;
        var id = Guid.NewGuid().ToString("N");
        db.LocalState.Add(new LocalState { Key = JournalIdKey, Value = id });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>The last journal sequence number given out (kept by SQLite even when entries are pruned).</summary>
    private static async Task<long> HeadAsync(GreenGlowDb db) =>
        await db.Database.SqlQueryRaw<long>("SELECT seq AS \"Value\" FROM sqlite_sequence WHERE name = 'SyncChanges'")
            .FirstOrDefaultAsync();

    public async Task<ChangesPage> ChangesAsync(long after, int limit)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var id = await JournalIdAsync(db);
        await using var read = await db.Database.BeginTransactionAsync();
        var head = await HeadAsync(db);
        var oldest = await db.SyncChanges.MinAsync(c => (long?)c.Seq) ?? head + 1;
        if (after > head || after < oldest - 1) return new ChangesPage(id, head, true, []);
        var entries = await db.SyncChanges.AsNoTracking().Where(c => c.Seq > after).OrderBy(c => c.Seq).Take(limit)
            .Select(c => new JournalEntry(c.Seq, c.Table, c.Key, c.Version, c.Data)).ToListAsync();
        return new ChangesPage(id, head, false, entries);
    }

    public async Task<Snapshot> SnapshotAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var id = await JournalIdAsync(db);
        // One read transaction: the rows, versions and head agree with each other.
        await using var read = await db.Database.BeginTransactionAsync();
        var head = await HeadAsync(db);
        var versions = await db.SyncVersions.AsNoTracking().ToDictionaryAsync(v => (v.Table, v.Key));
        var model = SyncModel.For(db.Model);
        var rows = new List<Change>();
        var present = new HashSet<(string, string)>();
        foreach (var table in model.Tables)
        foreach (var entity in table.All(db))
        {
            var key = table.KeyOf(entity);
            present.Add((table.Name, key));
            rows.Add(new Change(table.Name, key, versions.GetValueOrDefault((table.Name, key))?.Version ?? Hlc.Zero,
                table.Serialize(entity).ToJsonString()));
        }
        rows.AddRange(versions.Values.Where(v => v.Deleted && !present.Contains((v.Table, v.Key)))
            .Select(v => new Change(v.Table, v.Key, v.Version, null)));
        return new Snapshot(id, head, rows);
    }

    /// <summary>
    /// Applies a peer's rows, newest version winning. With journal, the ones applied go into this
    /// node's journal too, so they reach peers that missed them. Returns how many were applied.
    /// </summary>
    public async Task<int> ApplyAsync(IReadOnlyList<Change> changes, bool journal, CancellationToken cancel = default)
    {
        if (changes.Count == 0) return 0;
        var tables = new HashSet<string>();
        var applied = 0;
        await using (var db = await dbs.CreateDbContextAsync(cancel))
        {
            await GatedAsync(() => WithoutForeignKeysAsync(db, async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancel);
                applied = await ApplyAllAsync(db, changes, journal, tables, cancel);
                await transaction.CommitAsync(cancel);
            }, cancel), cancel);
        }
        if (tables.Count > 0) Applied?.Invoke(tables);
        return applied;
    }

    /// <summary>
    /// Resync from scratch: empties the synced tables and the journal, then rebuilds them from the
    /// peers' snapshots, in one transaction. The journal gets a new id, so peers read it afresh.
    /// </summary>
    public async Task ReplaceAsync(IReadOnlyList<Snapshot> snapshots, CancellationToken cancel = default)
    {
        var tables = new HashSet<string>();
        await using (var db = await dbs.CreateDbContextAsync(cancel))
        {
            // Applying from the start: this context's saves are under the gate already.
            db.Applying = true;
            var model = SyncModel.For(db.Model);
            tables.UnionWith(model.Tables.Select(t => t.Name));
            await GatedAsync(() => WithoutForeignKeysAsync(db, async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancel);
                // Table names come from the EF model, never from a peer.
#pragma warning disable EF1002
                foreach (var table in model.Tables.Reverse())
                    await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table.Name}\"", cancel);
#pragma warning restore EF1002
                await db.SyncVersions.ExecuteDeleteAsync(cancel);
                await db.SyncChanges.ExecuteDeleteAsync(cancel);
                await db.PeerCursors.ExecuteDeleteAsync(cancel);
                await db.LocalState.Where(s => s.Key == JournalIdKey).ExecuteDeleteAsync(cancel);
                db.LocalState.Add(new LocalState { Key = JournalIdKey, Value = Guid.NewGuid().ToString("N") });
                await db.SaveChangesAsync(cancel);
                foreach (var snapshot in snapshots)
                    await ApplyAllAsync(db, Ordered(model, snapshot.Rows), false, tables, cancel);
                await transaction.CommitAsync(cancel);
            }, cancel), cancel);
        }
        Applied?.Invoke(tables);
    }

    /// <summary>Merges a peer's snapshot, newest version winning; not journalled (peers read it from that peer).</summary>
    public async Task<int> MergeAsync(Snapshot snapshot, CancellationToken cancel = default)
    {
        SyncModel model;
        await using (var db = await dbs.CreateDbContextAsync(cancel)) model = SyncModel.For(db.Model);
        return await ApplyAsync(Ordered(model, snapshot.Rows), journal: false, cancel);
    }

    /// <summary>A snapshot's rows with parents first, so children find them.</summary>
    private static List<Change> Ordered(SyncModel model, IReadOnlyList<Change> rows) =>
        rows.OrderBy(r => model.Find(r.Table)?.Rank ?? int.MaxValue).ToList();

    private async Task<int> ApplyAllAsync(GreenGlowDb db, IReadOnlyList<Change> changes, bool journal, HashSet<string> tables,
        CancellationToken cancel)
    {
        db.Applying = true;
        var model = SyncModel.For(db.Model);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var applied = 0;
        foreach (var change in changes)
        {
            try
            {
                var done = await ApplyOneAsync(db, model, change, cancel);
                if (done && journal)
                    db.SyncChanges.Add(new SyncChange
                    {
                        Table = change.Table,
                        Key = change.Key,
                        Version = change.Version,
                        Data = change.Data,
                        At = now,
                    });
                await db.SaveChangesAsync(cancel);
                hlc.Witness(change.Version);
                if (done)
                {
                    applied++;
                    tables.Add(change.Table);
                }
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException
                                          or DbUpdateException)
            {
                // A row this version can't take: skip it rather than stop syncing everything else.
                log.LogWarning("sync: skipped a {Table} row: {Error}", change.Table, e.GetType().Name);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return applied;
    }

    private static async Task<bool> ApplyOneAsync(GreenGlowDb db, SyncModel model, Change change, CancellationToken cancel)
    {
        // A table or key this version doesn't have (a newer peer) is skipped.
        if (!Hlc.IsValid(change.Version) || model.Find(change.Table) is not { } table || table.KeyValues(change.Key) is not { } keys)
            return false;
        var known = await db.SyncVersions.FindAsync([table.Name, change.Key], cancel);
        if (known is not null && Hlc.Compare(known.Version, change.Version) >= 0) return false;
        var existing = await db.FindAsync(table.Clr, keys, cancel);

        if (change.Data is null)
        {
            if (existing is not null) db.Remove(existing);
            await SetVersionAsync(db, table, change.Key, change.Version, true, cancel);
            return true;
        }

        var data = JsonNode.Parse(change.Data)?.AsObject() ?? throw new JsonException("no row");
        object row;
        if (existing is null)
        {
            row = table.Create(data);
            for (var i = 0; i < keys.Length; i++) table.Key[i].PropertyInfo!.SetValue(row, keys[i]);
            // A row whose parent was deleted stays deleted.
            if (await ParentDeletedAsync(db, model, table, row, cancel))
            {
                await SetVersionAsync(db, table, change.Key, change.Version, true, cancel);
                return false;
            }
            db.Add(row);
        }
        else
        {
            table.Fill(existing, data, keys: false);
            row = existing;
        }

        // A unique rule allows only one of two rows: the newer version stays.
        foreach (var index in table.UniqueIndexes)
        {
            var equal = index.Properties.Select(p => ((Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty)p, SyncTable.ValueOf(row, p))).ToList();
            if (equal.Any(e => e.Item2 is null)) continue;
            foreach (var other in SyncTable.Where(db, table.Entity, equal).Where(o => table.KeyOf(o) != change.Key).ToList())
            {
                var otherKey = table.KeyOf(other);
                var otherVersion = (await db.SyncVersions.FindAsync([table.Name, otherKey], cancel))?.Version ?? Hlc.Zero;
                if (Hlc.Compare(change.Version, otherVersion) > 0)
                {
                    await RemoveAsync(db, model, table, other, otherKey, otherVersion, cancel);
                    continue;
                }
                // This row loses: it goes, with what hangs off it.
                await RemoveAsync(db, model, table, row, change.Key, change.Version, cancel);
                return existing is not null;
            }
        }
        await SetVersionAsync(db, table, change.Key, change.Version, false, cancel);
        return true;
    }

    /// <summary>Removes a row and the rows that hang off it, leaving tombstones at their own versions.</summary>
    private static async Task RemoveAsync(GreenGlowDb db, SyncModel model, SyncTable table, object row, string key, string version,
        CancellationToken cancel)
    {
        foreach (var foreignKey in table.Children)
        {
            if (model.For(foreignKey.DeclaringEntityType.ClrType) is not { } child) continue;
            var equal = foreignKey.Properties
                .Select((p, i) => ((Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty)p, SyncTable.ValueOf(row, foreignKey.PrincipalKey.Properties[i])))
                .ToList();
            foreach (var dependent in SyncTable.Where(db, child.Entity, equal))
            {
                var dependentKey = child.KeyOf(dependent);
                var dependentVersion = (await db.SyncVersions.FindAsync([child.Name, dependentKey], cancel))?.Version ?? Hlc.Zero;
                await RemoveAsync(db, model, child, dependent, dependentKey, dependentVersion, cancel);
            }
        }
        db.Remove(row);
        await SetVersionAsync(db, table, key, version, true, cancel);
    }

    private static async Task<bool> ParentDeletedAsync(GreenGlowDb db, SyncModel model, SyncTable table, object row,
        CancellationToken cancel)
    {
        foreach (var foreignKey in table.Parents)
        {
            if (model.For(foreignKey.PrincipalEntityType.ClrType) is not { } parent) continue;
            var values = foreignKey.Properties.Select(p => SyncTable.ValueOf(row, p)).ToArray();
            if (values.Any(v => v is null)) continue;
            var parentKey = SyncTable.KeyText(values);
            if ((await db.SyncVersions.FindAsync([parent.Name, parentKey], cancel))?.Deleted == true
                && await db.FindAsync(parent.Clr, values!, cancel) is null)
                return true;
        }
        return false;
    }

    private static async Task SetVersionAsync(GreenGlowDb db, SyncTable table, string key, string version, bool deleted,
        CancellationToken cancel)
    {
        var known = await db.SyncVersions.FindAsync([table.Name, key], cancel);
        if (known is null) db.SyncVersions.Add(new SyncVersion { Table = table.Name, Key = key, Version = version, Deleted = deleted });
        else (known.Version, known.Deleted) = (version, deleted);
    }

    /// <summary>
    /// Holds the write gate, so this node's own saves don't read or write row versions while a
    /// peer's rows go in (the two would both make a row's first version).
    /// </summary>
    private async Task GatedAsync(Func<Task> work, CancellationToken cancel)
    {
        await signals.WriteGate.WaitAsync(cancel);
        try
        {
            await work();
        }
        finally
        {
            signals.WriteGate.Release();
        }
    }

    /// <summary>
    /// Runs with SQLite's foreign key checks off: rows from peers may arrive before their parents,
    /// and deletes are applied row by row as the journal has them, never by cascade.
    /// </summary>
    private static async Task WithoutForeignKeysAsync(GreenGlowDb db, Func<Task> work, CancellationToken cancel)
    {
        await db.Database.OpenConnectionAsync(cancel);
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", cancel);
            await work();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Connection log rows written or updated here after a cursor.</summary>
    public async Task<LogsPage> LogsAsync(long after, int limit)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var changes = await db.LogChanges.AsNoTracking().Where(c => c.Seq > after).OrderBy(c => c.Seq).Take(limit)
            .Select(c => new { c.Seq, c.LogId }).ToListAsync();
        var ids = changes.Select(c => c.LogId).Distinct().ToList();
        var rows = await db.Logs.AsNoTracking().Where(l => ids.Contains(l.Id)).ToListAsync();
        var model = SyncModel.For(db.Model);
        return new LogsPage(await JournalIdAsync(db), changes.Count == 0 ? after : changes[^1].Seq, changes.Count == limit,
            rows.Select(r => model.Logs.Serialize(r)).ToList());
    }

    /// <summary>Applies a peer's log rows: a row's newer version wins (only its own node changes it).</summary>
    public async Task<int> ApplyLogsAsync(IReadOnlyList<JsonObject> rows, CancellationToken cancel = default)
    {
        if (rows.Count == 0) return 0;
        await using var db = await dbs.CreateDbContextAsync(cancel);
        db.Applying = true;
        var model = SyncModel.For(db.Model);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var applied = 0;
        foreach (var data in rows)
        {
            try
            {
                var incoming = (ConnectionLog)model.Logs.Create(data);
                if (!Hlc.IsValid(incoming.Version)) continue;
                var existing = await db.Logs.FindAsync([incoming.Id], cancel);
                if (existing is null) db.Logs.Add(incoming);
                else if (string.CompareOrdinal(incoming.Version, existing.Version) > 0) model.Logs.Fill(existing, data, keys: false);
                else continue;
                db.LogChanges.Add(new LogChange { LogId = incoming.Id, At = now });
                applied++;
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException)
            {
                log.LogWarning("sync: skipped a log row: {Error}", e.GetType().Name);
            }
        }
        await GatedAsync(() => db.SaveChangesAsync(cancel), cancel);
        return applied;
    }

    public async Task<PeerCursor> CursorAsync(int node)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.PeerCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Node == node) ?? new PeerCursor { Node = node };
    }

    public async Task SaveCursorAsync(PeerCursor cursor)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.PeerCursors.FindAsync(cursor.Node);
        if (row is null)
            db.PeerCursors.Add(new PeerCursor
            {
                Node = cursor.Node,
                JournalId = cursor.JournalId,
                Changes = cursor.Changes,
                LogsJournalId = cursor.LogsJournalId,
                Logs = cursor.Logs,
            });
        else (row.JournalId, row.Changes, row.LogsJournalId, row.Logs) = (cursor.JournalId, cursor.Changes, cursor.LogsJournalId, cursor.Logs);
        await db.SaveChangesAsync();
    }

    /// <summary>Drops journal entries older than JournalKept.</summary>
    public async Task PruneAsync()
    {
        var cutoff = (clock.GetUtcNow() - JournalKept).ToUnixTimeMilliseconds();
        await using var db = await dbs.CreateDbContextAsync();
        await db.SyncChanges.Where(c => c.At < cutoff).ExecuteDeleteAsync();
        await db.LogChanges.Where(c => c.At < cutoff).ExecuteDeleteAsync();
    }
}
