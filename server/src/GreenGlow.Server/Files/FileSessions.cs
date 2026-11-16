using System.Collections.Concurrent;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Files;

/// <summary>
/// One user's signed-on file server connection to one system. One request at a time uses it
/// (<see cref="UseAsync"/>): the file server answers in order, so two downloads at once would
/// read each other's replies.
/// </summary>
public sealed class FileSession
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private long _lastUsed;
    private int _downloaded;
    private int _ended;

    internal FileSession(FileServerClient client, string connection, Guid logId, DateTimeOffset now)
    {
        Client = client;
        Connection = connection;
        LogId = logId;
        _lastUsed = now.UtcTicks;
    }

    public FileServerClient Client { get; }

    /// <summary>The connection's name.</summary>
    public string Connection { get; }

    /// <summary>The IBM i user signed on, upper case.</summary>
    public string IbmUser => Client.User;

    internal Guid LogId { get; }

    internal DateTimeOffset LastUsed => new(Interlocked.Read(ref _lastUsed), TimeSpan.Zero);

    internal int Downloaded => Volatile.Read(ref _downloaded);

    internal void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastUsed, now.UtcTicks);

    internal void Downloading() => Interlocked.Increment(ref _downloaded);

    /// <summary>True the first time only: the caller ends it.</summary>
    internal bool Ending() => Interlocked.Exchange(ref _ended, 1) == 0;

    /// <summary>Waits for the connection, then holds it until the returned value is disposed.</summary>
    public async Task<IDisposable> UseAsync(CancellationToken cancel)
    {
        await _one.WaitAsync(cancel);
        return new Release(_one);
    }

    private sealed class Release(SemaphoreSlim one) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) one.Release();
        }
    }
}

/// <summary>
/// The file sessions, one per GreenGlow user and system, held in memory only. Each ends after
/// Files:IdleMinutes (15) without use, when its user signs off, when its connection is lost, or
/// when the server stops; a new one signs on again. Each is a row in the connection log (kind
/// files) with the IBM i user and how many files were downloaded; refused sign-ons are rows too.
/// No file's path, name or contents is logged.
/// </summary>
public sealed class FileSessions : IAsyncDisposable
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(string User, string Connection), FileSession> _sessions = new();
    private readonly HostConnector _connector;
    private readonly HostEndpoints _endpoints;
    private readonly ConnectionLogger _log;
    private readonly TimeProvider _clock;
    private readonly FilesOptions _options;
    private readonly ILogger _logger;
    private readonly ITimer _sweep;

    public FileSessions(HostConnector connector, HostEndpoints endpoints, ConnectionLogger log, TimeProvider clock,
        FilesOptions options, ILogger<FileSessions> logger)
    {
        _connector = connector;
        _endpoints = endpoints;
        _log = log;
        _clock = clock;
        _options = options;
        _logger = logger;
        _sweep = clock.CreateTimer(_ => _ = SweepAsync(), null, SweepEvery, SweepEvery);
    }

    public TimeSpan Idle => TimeSpan.FromMinutes(Math.Max(1, _options.IdleMinutes));

    /// <summary>
    /// Signs on (once; see <see cref="HostServerClient.SignOnAsync"/>) and keeps the session,
    /// replacing any the user had on that system. The password is not kept.
    /// </summary>
    public async Task<FileSession> SignOnAsync(CurrentUser user, Target target, string? clientIp, string ibmUser,
        string password, CancellationToken cancel)
    {
        FileServerClient client;
        try
        {
            client = await FileServerClient.ConnectAsync(_connector, _endpoints, target, ibmUser, password, cancel);
        }
        catch (SignOnException e) when (e.Failure is not (SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword) || e.Code != 0)
        {
            // Every attempt that reached the IBM i, or tried to, is in the log.
            var refused = await _log.StartAsync(Row(user, target, clientIp, ibmUser.Trim().ToUpperInvariant()));
            await _log.EndAsync(refused, $"sign-on failed: {e.Failure.ToString().ToLowerInvariant()}");
            _logger.LogInformation("file server sign-on to {Connection} for {User} failed: {Failure}", target.Name, user.Upn, e.Failure);
            throw;
        }

        var logId = await _log.StartAsync(Row(user, target, clientIp, client.User));
        var session = new FileSession(client, target.Name, logId, _clock.GetUtcNow());
        FileSession? replaced = null;
        _sessions.AddOrUpdate((user.Id, target.Name), session, (_, old) =>
        {
            replaced = old;
            return session;
        });
        _logger.LogInformation("file server sign-on to {Connection} for {User} as {IbmUser}", target.Name, user.Upn, client.User);
        if (replaced is not null) await EndAsync(replaced, "signed on again");
        return session;
    }

    /// <summary>The user's file session on a system, if any; finding it counts as using it.</summary>
    public FileSession? Find(string userId, string connection)
    {
        if (!_sessions.TryGetValue((userId, connection), out var session)) return null;
        session.Touch(_clock.GetUtcNow());
        return session;
    }

    public async Task<bool> SignOffAsync(string userId, string connection, string reason)
    {
        if (!_sessions.TryGetValue((userId, connection), out var session)) return false;
        await EndAsync(session, reason);
        return true;
    }

    /// <summary>Ends a session (once), closes its connection and closes its log row.</summary>
    public async Task EndAsync(FileSession session, string reason)
    {
        foreach (var entry in _sessions.Where(e => ReferenceEquals(e.Value, session)))
            _sessions.TryRemove(entry);
        if (!session.Ending()) return;
        await session.Client.DisposeAsync();
        await _log.UpdateAsync(session.LogId, row => row.FilesOpened = session.Downloaded);
        await _log.EndAsync(session.LogId, reason);
        _logger.LogInformation("file session on {Connection} ended: {Reason}", session.Connection, reason);
    }

    /// <summary>Ends the sessions unused for longer than the idle time.</summary>
    internal async Task SweepAsync()
    {
        var now = _clock.GetUtcNow();
        foreach (var session in _sessions.Values.Where(s => now - s.LastUsed >= Idle).ToList())
        {
            try
            {
                await EndAsync(session, "idle");
            }
            catch (Exception e)
            {
                _logger.LogWarning("ending an idle file session failed: {Error}", e.GetType().Name);
            }
        }
    }

    /// <summary>Ends every file session and closes its log row, while the server stops and the database can still be written.</summary>
    public async Task StopAsync()
    {
        await _sweep.DisposeAsync();
        foreach (var session in _sessions.Values.ToList()) await EndAsync(session, "server stopped");
    }

    /// <summary>Closes whatever is left without logging: by now the database may be gone.</summary>
    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync();
        var left = _sessions.Values.ToList();
        _sessions.Clear();
        foreach (var session in left)
            if (session.Ending()) await session.Client.DisposeAsync();
    }

    private static ConnectionLog Row(CurrentUser user, Target target, string? clientIp, string ibmUser) => new()
    {
        Kind = LogKind.Files,
        SessionId = Guid.NewGuid(),
        UserId = user.Id,
        Upn = user.Upn,
        ClientIp = clientIp,
        ConnectionName = target.Name,
        IbmUser = ibmUser.Length is > 0 and <= 10 ? ibmUser : null,
        FilesOpened = 0,
    };
}
