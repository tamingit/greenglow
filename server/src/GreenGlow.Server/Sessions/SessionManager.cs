using System.Collections.Concurrent;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Cluster;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Sessions;

/// <summary>Why a session couldn't be opened, for the API to report.</summary>
public sealed class SessionRefusedException(string code, string message) : Exception(message)
{
    /// <summary>A stable code the browser can act on: limit, server-full, no-device-name, draining.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The live host sessions on this node. Each user may hold up to the connection's
/// MaxSessionsPerUser sessions on a system. Device names are GG, the node's number and a session
/// number (GG1002): the lowest number no session on this node holds on that IBM i, whoever's it
/// is, so names are reused and never collide with another node's. A device
/// description belongs to the IBM i, not to a connection, so sessions through two connections to
/// one host (say TLS and Telnet) never share a name. A session that ends signed on, or with its
/// device in use, leaves its name kept for its user for a while, for the IBM i's reconnect.
/// </summary>
public sealed class SessionManager(
    HostConnector connector,
    SettingsStore settings,
    ConnectionLogger connectionLog,
    ClusterNode node,
    TimeProvider clock,
    ILoggerFactory loggers) : IAsyncDisposable
{
    /// <summary>The refusal code of a draining node.</summary>
    public const string Draining = "draining";

    public const int MaxDeviceNumber = 999;

    /// <summary>
    /// How long a name is kept for its user after a session ends signed on or in use: QDSCJOBITV,
    /// how long the IBM i keeps the disconnected job.
    /// </summary>
    public static readonly TimeSpan KeptForReconnect = TimeSpan.FromMinutes(5);

    /// <summary>Two targets reach the same IBM i: the same host, whatever the port or connection.</summary>
    private static bool SameSystem(Target a, Target b) => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    private static string SystemOf(Target target) => target.Host.ToLowerInvariant();

    public static string DeviceName(int node, int number) => $"GG{node}{number:D3}";

    private readonly ConcurrentDictionary<Guid, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Guid> _logIds = new();

    /// <summary>Names kept for a user's reconnect, by IBM i host and name, until a time. Guarded by _lock.</summary>
    private readonly Dictionary<(string System, string Device), (string UserId, DateTimeOffset Until)> _kept = new();
    private readonly Lock _lock = new();
    private readonly ILogger _log = loggers.CreateLogger<SessionManager>();

    public IEnumerable<HostSession> All => _sessions.Values.Where(s => !s.Ended);

    /// <summary>
    /// Raised when something a session list shows changes: a session opened or ended, a browser
    /// came or went, the title, device or IBM i user changed.
    /// </summary>
    public event Action<HostSession>? Changed;

    public HostSession? Find(Guid id) => _sessions.TryGetValue(id, out var session) && !session.Ended ? session : null;

    /// <summary>The live sessions a user opened.</summary>
    public IReadOnlyList<HostSession> OwnedBy(string userId) =>
        All.Where(s => s.Owner.Id == userId).OrderBy(s => s.CreatedAt).ToList();

    /// <summary>
    /// Opens a session on a target for a user, with a device name kept for them if there is one,
    /// else the lowest free one. A spare is the lowest free name not kept for anyone.
    /// </summary>
    public async Task<HostSession> CreateAsync(CurrentUser owner, Target target, string? clientIp, bool spare = false)
    {
        HostSession session;
        lock (_lock)
        {
            if (node.Draining)
                throw new SessionRefusedException(Draining, "This server is about to restart. Open the session again to use another one.");
            if (All.Count() >= settings.MaxConnections)
                throw new SessionRefusedException("server-full", "The server has reached its session limit.");
            var mine = All.Where(s => s.Owner.Id == owner.Id && s.Target.Name == target.Name).ToList();
            if (mine.Count >= target.MaxSessionsPerUser)
                throw new SessionRefusedException("limit",
                    $"You already have {mine.Count} session{(mine.Count == 1 ? "" : "s")} on {target.Label}, the most allowed.");
            var device = PickDevice(owner, target, spare);
            var id = Guid.NewGuid();
            session = new HostSession(id, owner, target, device, clock.GetUtcNow(),
                cancel => connector.ConnectAsync(target, cancel), clock, loggers.CreateLogger<HostSession>());
            _sessions[id] = session;
        }

        _logIds[session.Id] = await connectionLog.StartAsync(new ConnectionLog
        {
            Kind = LogKind.Session,
            SessionId = session.Id,
            UserId = owner.Id,
            Upn = owner.Upn,
            ClientIp = clientIp,
            ConnectionName = target.Name,
            DeviceName = session.DeviceName,
        });
        session.Finished += OnFinished;
        session.FrontendsChanged += OnFrontendsChanged;
        session.FactsChanged += OnFactsChanged;
        session.TitleChanged += s => Changed?.Invoke(s);
        session.Start();
        // A browser normally attaches at once; one that never does is treated as having left,
        // with at least AttachGrace to get there.
        session.CloseWhenIdle(IdleTime > AttachGrace ? IdleTime : AttachGrace);
        _log.LogInformation("session open {Id} {Target} {User} {Device}", session.Id, target.Name, owner.Upn, session.DeviceName);
        Changed?.Invoke(session);
        return session;
    }

    /// <summary>Called under _lock.</summary>
    private string PickDevice(CurrentUser owner, Target target, bool spare)
    {
        var now = clock.GetUtcNow();
        foreach (var expired in _kept.Where(k => k.Value.Until <= now).Select(k => k.Key).ToList()) _kept.Remove(expired);
        var system = SystemOf(target);
        var held = All.Where(s => SameSystem(s.Target, target)).Select(s => s.DeviceName).ToHashSet();
        if (!spare)
        {
            var mine = _kept.Where(k => k.Key.System == system && k.Value.UserId == owner.Id && !held.Contains(k.Key.Device))
                .Select(k => k.Key.Device).Order(StringComparer.Ordinal).FirstOrDefault();
            if (mine is not null)
            {
                _kept.Remove((system, mine));
                return mine;
            }
        }
        return Enumerable.Range(1, MaxDeviceNumber).Select(n => DeviceName(node.Id, n))
                   .FirstOrDefault(n => !held.Contains(n) && !_kept.ContainsKey((system, n)))
               ?? throw new SessionRefusedException("no-device-name", $"All this server's device names on {target.Label} are in use.");
    }

    /// <summary>
    /// A session that ended signed on leaves a disconnected job on the IBM i, and one that ended
    /// with its device in use leaves the name busy: either way the name is kept for its user.
    /// </summary>
    private void KeepForReconnect(HostSession session)
    {
        if (session.DeviceName is not { } device || !(session.SignedOn || session.DeviceWasInUse)) return;
        lock (_lock) _kept[(SystemOf(session.Target), device)] = (session.Owner.Id, clock.GetUtcNow() + KeptForReconnect);
    }

    private void OnFinished(HostSession session)
    {
        KeepForReconnect(session);
        _sessions.TryRemove(session.Id, out _);
        if (_logIds.TryRemove(session.Id, out var logId)) _ = EndLogAsync(logId, session.EndReason ?? "ended");
        Changed?.Invoke(session);
    }

    /// <summary>Closes a session's log row; a failure (the server stopping, say) is logged, not thrown.</summary>
    private async Task EndLogAsync(Guid logId, string reason)
    {
        try
        {
            await connectionLog.EndAsync(logId, reason);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning("could not close log row {LogId}: {Error}", logId, e.GetType().Name);
        }
    }

    private void OnFactsChanged(HostSession session)
    {
        Changed?.Invoke(session);
        if (_logIds.TryGetValue(session.Id, out var logId))
            _ = connectionLog.UpdateAsync(logId, row => row.DeviceName = session.DeviceName);
    }

    /// <summary>
    /// With no browser attached, the session stays open for Sessions.IdleMinutes so the user
    /// can come back to it, then ends.
    /// </summary>
    private void OnFrontendsChanged(HostSession session, int count)
    {
        if (count == 0) session.CloseWhenIdle(IdleTime);
        Changed?.Invoke(session);
    }

    /// <summary>How long a new session waits for its first browser, at the least.</summary>
    public static readonly TimeSpan AttachGrace = TimeSpan.FromMinutes(1);

    private TimeSpan IdleTime => TimeSpan.FromMinutes(settings.IdleMinutes);

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_sessions.Values.Select(s => s.DisposeAsync().AsTask()));
    }
}
