using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Cluster;

/// <summary>What this node knows about syncing with one peer, for the admin page.</summary>
public sealed class PeerSyncState
{
    public DateTimeOffset? LastSync { get; internal set; }
    public string? LastError { get; internal set; }
    public DateTimeOffset? LastErrorAt { get; internal set; }
    internal volatile bool Due = true;
    internal DateTimeOffset NextChanges;
    internal DateTimeOffset NextLogs;
}

/// <summary>
/// GreenGlow to GreenGlow sync: at start, merges every reachable peer's
/// snapshot; then pulls each peer's journal when poked and every PullSeconds, and its log rows
/// every LogPullSeconds; and pokes the peers when this node writes. One sync operation runs at a
/// time.
/// </summary>
public sealed class SyncService : BackgroundService
{
    public const int ChangesPerPage = 500;
    public const int LogsPerPage = 1000;
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PokeTimeout = TimeSpan.FromSeconds(3);

    private readonly ClusterOptions _options;
    private readonly PeerClient _peers;
    private readonly SyncStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<SyncService> _log;
    private readonly ConcurrentDictionary<int, PeerSyncState> _states = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _pokePending;

    public SyncService(ClusterOptions options, PeerClient peers, SyncStore store, SyncSignals signals, TimeProvider clock,
        ILogger<SyncService> log)
    {
        (_options, _peers, _store, _clock, _log) = (options, peers, store, clock, log);
        foreach (var id in options.PeerUrls.Keys) _states[id] = new PeerSyncState();
        signals.Changed += OnLocalChange;
    }

    public IReadOnlyDictionary<int, PeerSyncState> Peers => _states;

    /// <summary>Before serving: merges a snapshot of every peer that answers.</summary>
    public async Task JoinAsync(CancellationToken cancel = default)
    {
        foreach (var peer in _states.Keys.Order())
        {
            await GuardedAsync(peer, () => MergeSnapshotAsync(peer, cancel), cancel);
            if (_states[peer].LastError is null)
                _log.LogInformation("sync: merged node {Peer}'s snapshot", peer);
        }
    }

    /// <summary>A peer wrote something: pull from it now.</summary>
    public void PullSoon(int peer)
    {
        if (!_states.TryGetValue(peer, out var state)) return;
        state.Due = true;
        Wake();
    }

    /// <summary>Pulls every peer's journal and log rows now (tests, and after a resync).</summary>
    public async Task PullNowAsync(CancellationToken cancel = default)
    {
        foreach (var peer in _states.Keys.Order())
        {
            await GuardedAsync(peer, () => PullChangesAsync(peer, cancel), cancel);
            await GuardedAsync(peer, () => PullLogsAsync(peer, cancel), cancel);
        }
    }

    /// <summary>
    /// Resync from scratch: takes every reachable peer's snapshot, then replaces this node's
    /// synced data with them. Refused when no peer answers, which would leave the node empty.
    /// </summary>
    public async Task<(bool Ok, string Message)> ResyncAsync(CancellationToken cancel = default)
    {
        if (_states.IsEmpty) return (false, "This server has no peers to resync from.");
        var snapshots = new List<(int Peer, Snapshot Snapshot)>();
        var failed = new List<string>();
        foreach (var peer in _states.Keys.Order())
        {
            try
            {
                snapshots.Add((peer, await _peers.CallAsync<Snapshot>(peer, "snapshot", new { }, SnapshotTimeout, cancel)));
            }
            catch (PeerException e)
            {
                failed.Add($"node {peer}: {e.Message}");
            }
        }
        if (snapshots.Count == 0) return (false, $"No peer answered, so nothing was changed ({string.Join("; ", failed)}).");
        await _gate.WaitAsync(cancel);
        try
        {
            await _store.ReplaceAsync(snapshots.Select(s => s.Snapshot).ToList(), cancel);
            foreach (var (peer, snapshot) in snapshots)
                await _store.SaveCursorAsync(new PeerCursor { Node = peer, JournalId = snapshot.JournalId, Changes = snapshot.Head });
        }
        finally
        {
            _gate.Release();
        }
        var now = _clock.GetUtcNow();
        foreach (var state in _states.Values) (state.Due, state.NextLogs) = (true, now);
        Wake();
        var from = string.Join(", ", snapshots.Select(s => s.Peer));
        _log.LogWarning("sync: resynced from scratch from node {Peers}", from);
        return (true, failed.Count == 0
            ? $"Resynced from node {from}."
            : $"Resynced from node {from}; {string.Join("; ", failed)}.");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var nextPrune = _clock.GetUtcNow();
        while (!stop.IsCancellationRequested)
        {
            var now = _clock.GetUtcNow();
            foreach (var (peer, state) in _states)
            {
                if (state.Due || now >= state.NextChanges)
                {
                    state.Due = false;
                    state.NextChanges = now + TimeSpan.FromSeconds(_options.PullSeconds);
                    await GuardedAsync(peer, () => PullChangesAsync(peer, stop), stop);
                }
                if (now >= state.NextLogs)
                {
                    state.NextLogs = now + TimeSpan.FromSeconds(_options.LogPullSeconds);
                    await GuardedAsync(peer, () => PullLogsAsync(peer, stop), stop);
                }
            }
            if (now >= nextPrune)
            {
                nextPrune = now + TimeSpan.FromDays(1);
                try
                {
                    await _store.PruneAsync();
                }
                catch (Exception e) when (e is SqliteException or DbUpdateException or InvalidOperationException)
                {
                    _log.LogWarning("sync: pruning the journal failed: {Error}", e.GetType().Name);
                }
            }
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MergeSnapshotAsync(int peer, CancellationToken cancel)
    {
        var snapshot = await _peers.CallAsync<Snapshot>(peer, "snapshot", new { }, SnapshotTimeout, cancel);
        await _store.MergeAsync(snapshot, cancel);
        var cursor = await _store.CursorAsync(peer);
        (cursor.JournalId, cursor.Changes) = (snapshot.JournalId, snapshot.Head);
        await _store.SaveCursorAsync(cursor);
    }

    private async Task PullChangesAsync(int peer, CancellationToken cancel)
    {
        var cursor = await _store.CursorAsync(peer);
        while (true)
        {
            var page = await _peers.CallAsync<ChangesPage>(peer, "changes", new { after = cursor.Changes, limit = ChangesPerPage },
                PullTimeout, cancel);
            // A new journal (its database was replaced or resynced), or entries gone from it.
            if (page.Resync || page.JournalId != cursor.JournalId)
            {
                await MergeSnapshotAsync(peer, cancel);
                return;
            }
            if (page.Entries.Count == 0) return;
            await _store.ApplyAsync(page.Entries.Select(e => e.ToChange()).ToList(), journal: true, cancel);
            cursor.Changes = page.Entries[^1].Seq;
            await _store.SaveCursorAsync(cursor);
            if (page.Entries.Count < ChangesPerPage) return;
        }
    }

    private async Task PullLogsAsync(int peer, CancellationToken cancel)
    {
        var cursor = await _store.CursorAsync(peer);
        while (true)
        {
            var page = await _peers.CallAsync<LogsPage>(peer, "logs", new { after = cursor.Logs, limit = LogsPerPage }, PullTimeout, cancel);
            if (page.JournalId != cursor.LogsJournalId && cursor.Logs > 0)
            {
                // Its log rows were numbered afresh: read them all again (applying is idempotent).
                (cursor.LogsJournalId, cursor.Logs) = (page.JournalId, 0);
                continue;
            }
            cursor.LogsJournalId = page.JournalId;
            await _store.ApplyLogsAsync(page.Rows, cancel);
            cursor.Logs = page.Next;
            await _store.SaveCursorAsync(cursor);
            if (!page.More) return;
        }
    }

    private async Task GuardedAsync(int peer, Func<Task> work, CancellationToken cancel)
    {
        var state = _states[peer];
        try
        {
            await _gate.WaitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await work();
            state.LastSync = _clock.GetUtcNow();
            state.LastError = null;
        }
        catch (PeerException e)
        {
            // Said once per new reason, not every ten seconds.
            if (state.LastError != e.Message) _log.LogWarning("sync: node {Peer}: {Error}", peer, e.Message);
            (state.LastError, state.LastErrorAt) = (e.Message, _clock.GetUtcNow());
        }
        catch (Exception e) when (e is SqliteException or DbUpdateException or JsonException or InvalidOperationException)
        {
            var message = $"Applying its changes failed ({e.GetType().Name}).";
            if (state.LastError != message) _log.LogWarning("sync: node {Peer}: {Error}", peer, message);
            (state.LastError, state.LastErrorAt) = (message, _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>This node wrote synced rows: poke every peer (batched over 100 ms) so it pulls now.</summary>
    private void OnLocalChange()
    {
        if (_states.IsEmpty || Interlocked.Exchange(ref _pokePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Interlocked.Exchange(ref _pokePending, 0);
            await Task.WhenAll(_states.Keys.Select(async peer =>
            {
                try
                {
                    await _peers.CallAsync<JsonElement>(peer, "poke", new { }, PokeTimeout);
                }
                catch (PeerException)
                {
                    // It pulls on its own schedule; the pull's own error is what the admin page shows.
                }
            }));
        });
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }
}
