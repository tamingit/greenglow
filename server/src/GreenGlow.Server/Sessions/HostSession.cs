using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using GreenGlow.Engine;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.Sessions;

public enum AttachResult
{
    Attached,

    /// <summary>The session already has as many browsers as allowed.</summary>
    Full,

    /// <summary>Someone it is shared with, while none of the owner's browsers is attached.</summary>
    OwnerAway,

    Ended,
}

/// <summary>What a browser attached to a session may do.</summary>
public enum FrontendRole
{
    /// <summary>The user who opened the session.</summary>
    Owner,

    /// <summary>Shared with full access: may take the keyboard.</summary>
    Full,

    /// <summary>Shared view-only: sees the screen, never sends keys.</summary>
    View,
}

/// <summary>
/// One live TN5250 session held by the server: the Telnet connection to the IBM i, the engine
/// that reads it, and the browsers attached to it. Everything that touches the engine runs on
/// the session's own loop, one message at a time, because the engine is not thread-safe.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    public const string TerminalType = "IBM-3477-FC";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long the people a session is shared with stay after the owner's last browser drops
    /// without a Disconnect (a reload, a lost network). After a Disconnect they go at once.
    /// </summary>
    public static readonly TimeSpan OwnerGrace = TimeSpan.FromSeconds(30);

    public const string OwnerLeftReason = "The owner left this session. You can join it again once they are back in it.";

    public const string TakenOverReason = "This session was opened in another browser or tab.";

    public const string OwnerAwayReason = "The owner isn't in this session now. You can join it once they are back in it.";

    /// <summary>Events the browser shows; raw host records stay on the server.</summary>
    private static readonly HashSet<string> ForwardedEvents =
    [
        "negotiated", "startup", "device-in-use", "device-mismatch", "query-answered", "keyboard-stuck",
        "negative-response", "unsupported", "data-stream-error", "protocol-error", "alarm",
    ];

    private readonly Channel<Func<Task>> _inbox = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly Connection _engine;
    private readonly List<Frontend> _frontends = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SignOnWatch _signOn = new();
    private readonly ILogger _log;
    private readonly Func<CancellationToken, Task<(TcpClient, Stream)>> _connect;
    private TcpClient? _client;
    private Stream? _host;
    private Frontend? _controller;
    private ScreenSnapshot? _screen;
    private string? _screenJson;
    private Task? _loop;
    private int _closed;
    private DateTimeOffset _lastActivity;
    private readonly TimeProvider _clock;
    private long _idleGeneration;
    private long _ownerGeneration;

    public HostSession(
        Guid id,
        CurrentUser owner,
        Target target,
        string? deviceName,
        DateTimeOffset createdAt,
        Func<CancellationToken, Task<(TcpClient, Stream)>> connect,
        TimeProvider clock,
        ILogger log)
    {
        _clock = clock;
        Id = id;
        Owner = owner;
        Target = target;
        DeviceName = deviceName;
        CreatedAt = createdAt;
        _lastActivity = createdAt;
        _connect = connect;
        _log = log;
        _engine = new Connection(new ConnectionConfig
        {
            TerminalType = TerminalType,
            DeviceName = deviceName,
        });
    }

    public Guid Id { get; }
    public CurrentUser Owner { get; }
    public Target Target { get; }

    /// <summary>The device the session asked for; the IBM i may report a different one.</summary>
    public string? DeviceName { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Set when the session ends, with why.</summary>
    public string? EndReason { get; private set; }

    public bool Ended => Volatile.Read(ref _closed) != 0;

    /// <summary>The title of the screen shown now; empty before the first screen.</summary>
    public string Title { get; private set; } = "";

    /// <summary>The IBM i user signed on, as typed on the sign-on screen, once past it.</summary>
    public string? IbmUser => _signOn.IbmUser;

    /// <summary>The IBM i answered that the device name is in use by another session (8902).</summary>
    public bool DeviceWasInUse { get; private set; }

    public bool SignedOn => _signOn.SignedOn;

    /// <summary>The browsers attached now.</summary>
    public int FrontendCount { get; private set; }

    /// <summary>The owner's own browsers attached now: what the browsers-per-session limit counts.</summary>
    public int OwnerFrontendCount { get; private set; }

    /// <summary>Whether one of the owner's browsers is attached. Only then may the people it is shared with view it.</summary>
    public bool OwnerPresent { get; private set; }

    /// <summary>Raised once when the session has ended (host closed, error, or Close).</summary>
    public event Action<HostSession>? Finished;

    /// <summary>Raised when the number of attached browsers changes, on the session's loop.</summary>
    public event Action<HostSession, int>? FrontendsChanged;

    /// <summary>Raised when the device or user learned from the stream changes.</summary>
    public event Action<HostSession>? FactsChanged;

    /// <summary>Raised when the screen's title changes, for the session list.</summary>
    public event Action<HostSession>? TitleChanged;

    /// <summary>Connects to the IBM i and starts reading. Faults end the session.</summary>
    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var reader = Task.Run(ReadHostAsync);
        try
        {
            await foreach (var work in _inbox.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    await work();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "session {Id} failed", Id);
                    await EndAsync("server error");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
        await reader;
    }

    private async Task ReadHostAsync()
    {
        try
        {
            (_client, _host) = await _connect(_stop.Token);
            var buffer = new byte[16 * 1024];
            while (!_stop.IsCancellationRequested)
            {
                var n = await _host.ReadAsync(buffer, _stop.Token);
                if (n == 0)
                {
                    Post(() => EndAsync("host closed"));
                    return;
                }
                var bytes = buffer.AsSpan(0, n).ToArray();
                Post(() => FromHostAsync(bytes));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Closed.
        }
        catch (OperationCanceledException)
        {
            Post(() => EndAsync("host error connect timeout"));
        }
        catch (Exception e) when (e is IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            var reason = $"host error {ErrorCode(e)}";
            Post(() => EndAsync(reason));
        }
    }

    internal static string ErrorCode(Exception e) => e switch
    {
        SocketException s => s.SocketErrorCode.ToString(),
        System.Security.Authentication.AuthenticationException => "certificate rejected",
        { InnerException: SocketException s } => s.SocketErrorCode.ToString(),
        TimeoutException or OperationCanceledException => "connect timeout",
        _ => "io",
    };

    private void Post(Func<Task> work)
    {
        if (!_inbox.Writer.TryWrite(work)) _log.LogDebug("session {Id} closed; message dropped", Id);
    }

    private async Task FromHostAsync(byte[] bytes)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await ApplyAsync(_engine.Receive(bytes));
    }

    /// <summary>Sends what the engine produced, then tells the browsers what changed.</summary>
    private async Task ApplyAsync(ReceiveResult result)
    {
        foreach (var send in result.Send)
        {
            if (_host is null) break;
            try
            {
                await _host.WriteAsync(send.Bytes, _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                await EndAsync($"host error {ErrorCode(e)}");
                return;
            }
        }
        foreach (var e in result.Events)
        {
            switch (e)
            {
                case ConnectionEvent.Screen screen:
                    Show(screen.Snapshot);
                    break;
                case ConnectionEvent.Startup startup when startup.Result.DeviceName is { Length: > 0 } actual
                                                      && actual != DeviceName:
                    DeviceName = actual;
                    FactsChanged?.Invoke(this);
                    Broadcast(EventMessage(e));
                    break;
                default:
                    if (e is ConnectionEvent.DeviceInUse) DeviceWasInUse = true;
                    if (ForwardedEvents.Contains(e.Type)) Broadcast(EventMessage(e));
                    break;
            }
        }
    }

    private void Show(ScreenSnapshot snapshot)
    {
        var before = (_signOn.IbmUser, _signOn.SignedOn);
        _screen = snapshot;
        _signOn.See(snapshot);
        var title = ScreenFacts.Title(snapshot);
        var retitled = title != Title;
        Title = title;
        if (before != (_signOn.IbmUser, _signOn.SignedOn)) FactsChanged?.Invoke(this);
        if (retitled) TitleChanged?.Invoke(this);
        // The title names the page's tab; the session list shows the same one. Signed on is the
        // server's: it has seen the session from its first screen, which a page joining later
        // hasn't, so the page asks before Disconnect and notices a sign-off by it.
        _screenJson = JsonSerializer.Serialize(new
        {
            type = "screen",
            title = Title,
            signedOn = _signOn.SignedOn,
            snapshot = ScreenFacts.Redact(snapshot),
        }, Json);
        foreach (var frontend in _frontends) frontend.Outbox.PostScreen(_screenJson);
    }

    private static string EventMessage(ConnectionEvent e) =>
        JsonSerializer.Serialize(new { type = "event", @event = (object)e }, Json);

    private void Broadcast(string message)
    {
        foreach (var frontend in _frontends) frontend.Outbox.Post(message);
    }

    /// <summary>
    /// Attaches a browser. The owner's own browsers are limited to `limit`; people the session
    /// is shared with don't count toward it (decided 12 October 2026): a share names at most 20
    /// people, and they come in only while the owner is in it. The owner (or, without one, the
    /// first full-access browser) gets the keyboard. With `takeOver`, the owner's own browsers
    /// elsewhere are sent away to make room.
    /// </summary>
    public Task<AttachResult> AttachAsync(Frontend frontend, int limit, bool takeOver = false)
    {
        var done = new TaskCompletionSource<AttachResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Ended || !_inbox.Writer.TryWrite(() =>
        {
            if (Ended)
            {
                done.SetResult(AttachResult.Ended);
                return Task.CompletedTask;
            }
            if (takeOver && frontend.Role == FrontendRole.Owner) MakeRoom(limit);
            if (frontend.Role == FrontendRole.Owner && OwnerFrontendCount >= limit)
            {
                done.SetResult(AttachResult.Full);
                return Task.CompletedTask;
            }
            if (frontend.Role != FrontendRole.Owner && !OwnerPresent)
            {
                done.SetResult(AttachResult.OwnerAway);
                return Task.CompletedTask;
            }
            _frontends.Add(frontend);
            Count();
            if (frontend.Role == FrontendRole.Owner)
            {
                OwnerPresent = true;
                _ownerGeneration++;
            }
            _idleGeneration++;
            IdleSince = null;
            if (_controller is null && frontend.Role != FrontendRole.View) _controller = frontend;
            frontend.Outbox.Post(JsonSerializer.Serialize(new
            {
                type = "hello",
                sessionId = Id,
                frontendId = frontend.Id,
                role = RoleName(frontend.Role),
                target = Target.Name,
                label = Target.Label,
                device = DeviceName,
            }, Json));
            if (_screenJson is not null) frontend.Outbox.PostScreen(_screenJson);
            PublishFrontends();
            BroadcastControl();
            FrontendsChanged?.Invoke(this, _frontends.Count);
            done.SetResult(AttachResult.Attached);
            return Task.CompletedTask;
        }))
            done.TrySetResult(AttachResult.Ended);
        return done.Task;
    }

    /// <summary>
    /// A browser left. When it was the owner's last, the people the session is shared with go
    /// too: at once when the owner disconnected, after <see cref="OwnerGrace"/> otherwise.
    /// </summary>
    public void Detach(Frontend frontend, bool disconnected = false) => Post(() =>
    {
        if (!_frontends.Remove(frontend)) return Task.CompletedTask;
        Count();
        PublishFrontends();
        if (frontend.Role == FrontendRole.Owner && _frontends.All(f => f.Role != FrontendRole.Owner))
        {
            OwnerPresent = false;
            var generation = ++_ownerGeneration;
            if (disconnected) SendAwayShared();
            else
                _ = Task.Delay(OwnerGrace, _clock, _stop.Token).ContinueWith(
                    t =>
                    {
                        if (t.IsCompletedSuccessfully)
                            Post(() =>
                            {
                                if (generation == _ownerGeneration) SendAwayShared();
                                return Task.CompletedTask;
                            });
                    },
                    TaskScheduler.Default);
        }
        if (_controller == frontend)
            _controller = _frontends.FirstOrDefault(f => f.Role == FrontendRole.Owner)
                          ?? _frontends.FirstOrDefault(f => f.Role == FrontendRole.Full);
        BroadcastControl();
        FrontendsChanged?.Invoke(this, _frontends.Count);
        return Task.CompletedTask;
    });

    /// <summary>
    /// The owner changed a shared person's access: their browsers that came in through that
    /// share take the new role at once. One made view-only loses the keyboard if it had it.
    /// </summary>
    public void SetShareRole(Guid shareId, string userId, FrontendRole role) => Post(() =>
    {
        var changed = false;
        foreach (var frontend in _frontends.Where(f => f.ShareId == shareId && f.User.Id == userId && f.Role != role))
        {
            frontend.Role = role;
            changed = true;
            frontend.Outbox.Post(JsonSerializer.Serialize(new { type = "role", role = RoleName(role) }, Json));
            if (role == FrontendRole.View && _controller == frontend)
                _controller = _frontends.FirstOrDefault(f => f.Role == FrontendRole.Owner)
                              ?? _frontends.FirstOrDefault(f => f.Role == FrontendRole.Full);
        }
        if (changed)
        {
            PublishFrontends();
            BroadcastControl();
        }
        return Task.CompletedTask;
    });

    /// <summary>
    /// The owner takes the session over from their own browsers elsewhere: sends away the
    /// earliest until there is room for one more. On the loop.
    /// </summary>
    /// <summary>Counts the browsers attached, all and the owner's. On the loop.</summary>
    private void Count()
    {
        FrontendCount = _frontends.Count;
        OwnerFrontendCount = _frontends.Count(f => f.Role == FrontendRole.Owner);
    }

    private void MakeRoom(int limit)
    {
        var closed = JsonSerializer.Serialize(new { type = "closed", reason = TakenOverReason }, Json);
        foreach (var other in _frontends.Where(f => f.Role == FrontendRole.Owner).Take(OwnerFrontendCount - limit + 1).ToList())
        {
            _frontends.Remove(other);
            if (_controller == other) _controller = null;
            other.Outbox.Close(closed);
        }
        Count();
    }

    /// <summary>Sends away every browser that came in through a share: the owner left. On the loop.</summary>
    private void SendAwayShared()
    {
        var closed = JsonSerializer.Serialize(new { type = "closed", reason = OwnerLeftReason }, Json);
        foreach (var frontend in _frontends.Where(f => f.Role != FrontendRole.Owner).ToList())
            frontend.Outbox.Close(closed);
    }

    /// <summary>Sends away one person's browsers that came in through a share.</summary>
    public void DetachSharedUser(Guid shareId, string userId) => Post(() =>
    {
        var closed = JsonSerializer.Serialize(new { type = "closed", reason = "The owner stopped sharing this session with you." }, Json);
        foreach (var frontend in _frontends.Where(f => f.ShareId == shareId && f.User.Id == userId).ToList())
            frontend.Outbox.Close(closed);
        return Task.CompletedTask;
    });

    /// <summary>Who is attached now, by user: for the owner's list of whom the session is shared with.</summary>
    public IReadOnlyList<(string UserId, Guid? ShareId, FrontendRole Role)> Attached => _attached;

    private IReadOnlyList<(string UserId, Guid? ShareId, FrontendRole Role)> _attached = [];

    /// <summary>Publishes who is attached for other threads to read; call on the loop after a change.</summary>
    private void PublishFrontends() => _attached = _frontends.Select(f => (f.User.Id, f.ShareId, f.Role)).ToList();

    /// <summary>Sends away the browsers that came in through a share that has been revoked.</summary>
    public void DetachShare(Guid shareId) => Post(() =>
    {
        var closed = JsonSerializer.Serialize(new { type = "closed", reason = "The owner stopped sharing this session." }, Json);
        foreach (var frontend in _frontends.Where(f => f.ShareId == shareId).ToList())
            frontend.Outbox.Close(closed);
        return Task.CompletedTask;
    });

    /// <summary>When the last browser left, while none is attached.</summary>
    public DateTimeOffset? IdleSince { get; private set; }

    /// <summary>
    /// Ends the session once it has had no browser for `after`, unless one attaches first.
    /// Zero ends it at once.
    /// </summary>
    public void CloseWhenIdle(TimeSpan after) => Post(async () =>
    {
        if (_frontends.Count > 0) return;
        var generation = ++_idleGeneration;
        IdleSince ??= _clock.GetUtcNow();
        if (after <= TimeSpan.Zero)
        {
            await EndAsync("no browser attached");
            return;
        }
        var reason = $"no browser attached for {FormatDuration(after)}";
        _ = Task.Delay(after, _clock, _stop.Token).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    Post(() => generation == _idleGeneration && _frontends.Count == 0 ? EndAsync(reason) : Task.CompletedTask);
            },
            TaskScheduler.Default);
    });

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.TotalMinutes % 1 == 0
            ? $"{span.TotalMinutes:0} minute{(span.TotalMinutes == 1 ? "" : "s")}"
            : $"{span.TotalSeconds:0} seconds";

    /// <summary>A key from a browser. Only the browser holding the keyboard may type.</summary>
    public void Key(Frontend frontend, Key key) => Post(async () =>
    {
        if (!_frontends.Contains(frontend)) return;
        if (frontend.Role == FrontendRole.View || _controller != frontend)
        {
            frontend.Outbox.Post(JsonSerializer.Serialize(new { type = "refused", reason = "not-controller" }, Json));
            return;
        }
        if (key is FunctionKey { Name: var name } && Keyboard.AidKeyNames.Contains(name)
                                                    && _engine.Screen.Keyboard == KeyboardState.Unlocked)
            _signOn.BeforeAid(_engine.Screen);
        _lastActivity = DateTimeOffset.UtcNow;
        await ApplyAsync(_engine.Key(key));
    });

    /// <summary>
    /// Pasted text, typed from the cursor as the user's own keys (see Paste). It stops at the
    /// first key the screen refuses (the keyboard in error, as for a protected position), or as
    /// soon as the keyboard isn't free (an auto-enter field sent the screen): nothing is typed
    /// ahead onto the next screen. The browser is told how much went in, and why it stopped.
    /// </summary>
    public void PasteText(Frontend frontend, string text) => Post(async () =>
    {
        if (!_frontends.Contains(frontend)) return;
        if (frontend.Role == FrontendRole.View || _controller != frontend)
        {
            frontend.Outbox.Post(JsonSerializer.Serialize(new { type = "refused", reason = "not-controller" }, Json));
            return;
        }
        var screen = _engine.Screen;
        var (keys, pastScreen) = Paste.Keys(text, screen.Cursor, screen.Rows);
        var characters = keys.Count(k => k is CharacterKey);
        var typed = 0;
        string? stopped = screen.Keyboard == KeyboardState.Unlocked ? null : "keyboard";
        foreach (var key in keys)
        {
            if (stopped is not null) break;
            _lastActivity = DateTimeOffset.UtcNow;
            var result = _engine.Key(key);
            await ApplyAsync(result);
            if (Ended) return;
            var now = _engine.Screen.Keyboard;
            if (now == KeyboardState.Error) stopped = "refused";
            else
            {
                if (key is CharacterKey) typed++;
                if (now != KeyboardState.Unlocked || result.Send.Count > 0) stopped = "keyboard";
            }
        }
        if (stopped is null && pastScreen) stopped = "screen-end";
        frontend.Outbox.Post(JsonSerializer.Serialize(new { type = "pasted", typed, of = characters, stopped }, Json));
    });

    /// <summary>A full-access browser takes the keyboard from whoever holds it.</summary>
    public void TakeControl(Frontend frontend) => Post(() =>
    {
        if (_frontends.Contains(frontend) && frontend.Role != FrontendRole.View && _controller != frontend)
        {
            _controller = frontend;
            BroadcastControl();
        }
        return Task.CompletedTask;
    });

    /// <summary>Someone the session is shared with gives the keyboard back to the owner.</summary>
    public void ReturnControl(Frontend frontend) => Post(() =>
    {
        if (_controller == frontend && frontend.Role != FrontendRole.Owner
            && _frontends.FirstOrDefault(f => f.Role == FrontendRole.Owner) is { } owner)
        {
            _controller = owner;
            BroadcastControl();
        }
        return Task.CompletedTask;
    });

    /// <summary>Who holds the keyboard, for other threads to read: their user and the share they came in through.</summary>
    public (string UserId, Guid? ShareId)? KeyboardHolder { get; private set; }

    private void BroadcastControl()
    {
        KeyboardHolder = _controller is null ? null : (_controller.User.Id, _controller.ShareId);
        foreach (var frontend in _frontends)
        {
            frontend.Outbox.Post(JsonSerializer.Serialize(new
            {
                type = "control",
                you = _controller == frontend,
                holder = _controller?.User.Name,
                frontends = _frontends.Count,
            }, Json));
        }
    }

    public static string RoleName(FrontendRole role) => role switch
    {
        FrontendRole.Owner => "owner",
        FrontendRole.Full => "full",
        _ => "view",
    };

    /// <summary>Ends the session: closes the IBM i connection and tells every browser.</summary>
    public void Close(string reason) => Post(() => EndAsync(reason));

    private async Task EndAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        EndReason = reason;
        _inbox.Writer.TryComplete();
        foreach (var e in _engine.Closed())
            if (ForwardedEvents.Contains(e.Type)) Broadcast(EventMessage(e));
        var closed = JsonSerializer.Serialize(new { type = "closed", reason }, Json);
        foreach (var frontend in _frontends) frontend.Outbox.Close(closed);
        _frontends.Clear();
        PublishFrontends();
        FrontendCount = 0;
        OwnerFrontendCount = 0;
        await _stop.CancelAsync();
        try
        {
            _host?.Dispose();
        }
        catch (IOException)
        {
            // Already gone.
        }
        _client?.Dispose();
        _log.LogInformation("session end {Id} {Target} {Reason}", Id, Target.Name, reason);
        Finished?.Invoke(this);
    }

    /// <summary>For tests and the manager: the current screen, read on the session's loop.</summary>
    public Task<ScreenSnapshot?> ScreenAsync()
    {
        var done = new TaskCompletionSource<ScreenSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inbox.Writer.TryWrite(() =>
            {
                done.SetResult(_screen);
                return Task.CompletedTask;
            }))
            done.SetResult(_screen);
        return done.Task;
    }

    public DateTimeOffset LastActivity => _lastActivity;

    public async ValueTask DisposeAsync()
    {
        Close("server shutting down");
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Abandon it; the process is stopping.
            }
        }
        _stop.Dispose();
    }
}
