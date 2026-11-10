using System.Collections.Concurrent;
using System.Security.Cryptography;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Printing;

/// <summary>
/// One user's signed-on Network Print server connection to one system. The
/// spooled files it listed get ids of their own, so a browser can open only what it was shown.
/// </summary>
public sealed class PrintSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _ids = [];
    private readonly Dictionary<string, SpooledFile> _files = [];
    private long _lastUsed;
    private int _filesOpened;

    internal PrintSession(NetworkPrintClient client, string connection, Guid logId, DateTimeOffset now)
    {
        Client = client;
        Connection = connection;
        LogId = logId;
        _lastUsed = now.UtcTicks;
    }

    public NetworkPrintClient Client { get; }

    /// <summary>The connection's name.</summary>
    public string Connection { get; }

    /// <summary>The IBM i user signed on, upper case.</summary>
    public string IbmUser => Client.User;

    internal Guid LogId { get; }

    internal DateTimeOffset LastUsed => new(Interlocked.Read(ref _lastUsed), TimeSpan.Zero);

    internal int FilesOpened => Volatile.Read(ref _filesOpened);

    internal void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastUsed, now.UtcTicks);

    internal void Opened() => Interlocked.Increment(ref _filesOpened);

    private int _ended;

    /// <summary>True the first time only: the caller ends it.</summary>
    internal bool Ending() => Interlocked.Exchange(ref _ended, 1) == 0;

    /// <summary>The id for a listed spooled file: the same one each time this session lists it.</summary>
    public string IdFor(SpooledFile file)
    {
        var key = Convert.ToHexString(file.Identity);
        lock (_gate)
        {
            if (!_ids.TryGetValue(key, out var id))
                _ids[key] = id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            _files[id] = file;
            return id;
        }
    }

    public SpooledFile? File(string id)
    {
        lock (_gate) return _files.GetValueOrDefault(id);
    }
}

/// <summary>
/// The print sessions, one per GreenGlow user and system, held in memory only. Each ends after
/// Printer:IdleMinutes (15) without use, when its user signs off, when its connection is lost,
/// or when the server stops; a new one asks for the password again. Each is a row in the
/// connection log, with the IBM i user and how many files were opened; refused sign-ons are
/// rows too. Nothing about a file's name or contents is logged.
/// </summary>
public sealed class PrintSessions : IAsyncDisposable
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(string User, string Connection), PrintSession> _sessions = new();
    private readonly HostConnector _connector;
    private readonly ConnectionLogger _log;
    private readonly TimeProvider _clock;
    private readonly PrinterOptions _options;
    private readonly ILogger _logger;
    private readonly ITimer _sweep;

    public PrintSessions(HostConnector connector, ConnectionLogger log, TimeProvider clock, PrinterOptions options,
        ILogger<PrintSessions> logger)
    {
        _connector = connector;
        _log = log;
        _clock = clock;
        _options = options;
        _logger = logger;
        _sweep = clock.CreateTimer(_ => _ = SweepAsync(), null, SweepEvery, SweepEvery);
    }

    public TimeSpan Idle => TimeSpan.FromMinutes(Math.Max(1, _options.IdleMinutes));

    /// <summary>The Network Print server for a connection: its host, over TLS when the connection is.</summary>
    public Target EndpointFor(Target telnet) => telnet with
    {
        Port = telnet.Secure
            ? _options.SecurePort ?? HostService.NetworkPrint.SecurePort
            : _options.Port ?? HostService.NetworkPrint.Port,
    };

    /// <summary>
    /// Signs on (once; see <see cref="HostServerClient.SignOnAsync"/>) and keeps the session,
    /// replacing any the user had on that system. The password is not kept.
    /// </summary>
    public async Task<PrintSession> SignOnAsync(CurrentUser user, Target target, string? clientIp, string ibmUser,
        string password, CancellationToken cancel)
    {
        NetworkPrintClient client;
        try
        {
            client = await NetworkPrintClient.ConnectAsync(_connector, EndpointFor(target), ibmUser, password, cancel);
        }
        catch (SignOnException e) when (e.Failure is not (SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword) || e.Code != 0)
        {
            // Every attempt that reached the IBM i, or tried to, is in the log.
            var refused = await _log.StartAsync(Row(user, target, clientIp, ibmUser.Trim().ToUpperInvariant()));
            await _log.EndAsync(refused, $"sign-on failed: {Words(e.Failure)}");
            _logger.LogInformation("printer sign-on to {Connection} for {User} failed: {Failure}", target.Name, user.Upn, e.Failure);
            throw;
        }

        var logId = await _log.StartAsync(Row(user, target, clientIp, client.User));
        var session = new PrintSession(client, target.Name, logId, _clock.GetUtcNow());
        PrintSession? replaced = null;
        _sessions.AddOrUpdate((user.Id, target.Name), session, (_, old) =>
        {
            replaced = old;
            return session;
        });
        _logger.LogInformation("printer sign-on to {Connection} for {User} as {IbmUser}", target.Name, user.Upn, client.User);
        if (replaced is not null) await EndAsync(replaced, "signed on again");
        return session;
    }

    /// <summary>The user's print session on a system, if any; finding it counts as using it.</summary>
    public PrintSession? Find(string userId, string connection)
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
    public async Task EndAsync(PrintSession session, string reason)
    {
        foreach (var entry in _sessions.Where(e => ReferenceEquals(e.Value, session)))
            _sessions.TryRemove(entry);
        if (!session.Ending()) return;
        await session.Client.DisposeAsync();
        await _log.UpdateAsync(session.LogId, row => row.FilesOpened = session.FilesOpened);
        await _log.EndAsync(session.LogId, reason);
        _logger.LogInformation("printer session on {Connection} ended: {Reason}", session.Connection, reason);
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
                _logger.LogWarning("ending an idle printer session failed: {Error}", e.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Ends every print session and closes its log row. Called while the server stops
    /// (ApplicationStopping), when the database can still be written.
    /// </summary>
    public async Task StopAsync()
    {
        await _sweep.DisposeAsync();
        foreach (var session in _sessions.Values.ToList()) await EndAsync(session, "server stopped");
    }

    /// <summary>
    /// Closes whatever is left without logging: by now the database may be gone. Rows left open
    /// are closed when the server starts again.
    /// </summary>
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
        Kind = LogKind.Printer,
        SessionId = Guid.NewGuid(),
        UserId = user.Id,
        Upn = user.Upn,
        ClientIp = clientIp,
        ConnectionName = target.Name,
        IbmUser = ibmUser.Length is > 0 and <= 10 ? ibmUser : null,
        FilesOpened = 0,
    };

    private static string Words(SignOnFailure failure) => failure switch
    {
        SignOnFailure.WrongPassword => "wrong password",
        SignOnFailure.WrongPasswordDisabled => "wrong password, profile disabled",
        SignOnFailure.ProfileDisabled => "profile disabled",
        SignOnFailure.PasswordExpired => "password expired",
        SignOnFailure.UnknownUser => "unknown user",
        SignOnFailure.NoPassword => "no password",
        SignOnFailure.Unreachable => "print server unreachable",
        _ => failure.ToString().ToLowerInvariant(),
    };
}
