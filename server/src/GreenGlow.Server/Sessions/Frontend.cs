using GreenGlow.Server.Auth;

namespace GreenGlow.Server.Sessions;

/// <summary>A browser attached to a host session.</summary>
/// <param name="ShareId">The share link a recipient came in through; null for the owner.</param>
public sealed class Frontend(CurrentUser user, FrontendRole role, string? clientIp, Guid? shareId = null)
{
    public Guid? ShareId { get; } = shareId;
    public Guid Id { get; } = Guid.NewGuid();
    public CurrentUser User { get; } = user;
    /// <summary>Changes on the session's loop when the owner changes a shared person's access.</summary>
    public FrontendRole Role { get; set; } = role;
    public string? ClientIp { get; } = clientIp;
    public Outbox Outbox { get; } = new();
}

/// <summary>
/// Messages waiting to go to one browser. A screen not yet sent is replaced by a newer one,
/// so a slow browser gets the latest screen rather than a backlog. Other messages keep their
/// order. A browser that falls too far behind is dropped.
/// </summary>
public sealed class Outbox
{
    public const int MaxPending = 500;

    private readonly LinkedList<(string Message, bool Screen)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _lock = new();
    private LinkedListNode<(string Message, bool Screen)>? _pendingScreen;
    private bool _closed;

    /// <summary>Set when the outbox stopped taking messages because the browser fell behind.</summary>
    public bool Overflowed { get; private set; }

    public void Post(string message) => Add(message, screen: false, close: false);

    public void PostScreen(string message) => Add(message, screen: true, close: false);

    /// <summary>Queues a last message; the sender stops after it.</summary>
    public void Close(string message) => Add(message, screen: false, close: true);

    private void Add(string message, bool screen, bool close)
    {
        lock (_lock)
        {
            if (_closed) return;
            if (screen && _pendingScreen is not null)
            {
                _pendingScreen.Value = (message, true);
                return;
            }
            if (_queue.Count >= MaxPending)
            {
                Overflowed = true;
                _closed = true;
                _signal.Release();
                return;
            }
            var node = _queue.AddLast((message, screen));
            if (screen) _pendingScreen = node;
            if (close) _closed = true;
        }
        _signal.Release();
    }

    /// <summary>The next message, or null once the outbox is closed and empty.</summary>
    public async Task<string?> NextAsync(CancellationToken cancel)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_queue.First is { } first)
                {
                    _queue.RemoveFirst();
                    if (_pendingScreen == first) _pendingScreen = null;
                    return Overflowed ? null : first.Value.Message;
                }
                if (_closed) return null;
            }
            await _signal.WaitAsync(cancel);
        }
    }
}
