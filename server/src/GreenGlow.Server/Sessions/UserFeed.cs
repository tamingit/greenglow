using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Sessions;

/// <summary>
/// One socket per page, /ws/user, not tied to a session: the server pushes the user's session
/// list (the shape of GET /api/sessions) whenever it changes, so the page needn't poll. A change
/// to a session goes to its owner and to the people named on its live shares; a share change
/// also goes to the people it took away. Changes are batched over <see cref="Batch"/>, since
/// titles change on every screen. Each list is built for its user alone, so only their own
/// sessions and those shared with them are ever sent.
/// </summary>
public sealed class UserFeed
{
    public static readonly TimeSpan Batch = TimeSpan.FromMilliseconds(500);

    private readonly SessionManager _sessions;
    private readonly Shares _shares;
    private readonly Presence _presence;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly List<Socket> _sockets = [];
    private readonly HashSet<Guid> _dirtySessions = [];
    /// <summary>Users to push to: their IDs, and "upn:" plus the UPN in lower case.</summary>
    private readonly HashSet<string> _dirtyUsers = [];
    private bool _scheduled;

    public UserFeed(SessionManager sessions, Shares shares, Presence presence, TimeProvider clock, ILogger<UserFeed> log)
    {
        _sessions = sessions;
        _shares = shares;
        _presence = presence;
        _clock = clock;
        _log = log;
        sessions.Changed += session => SessionChanged(session.Id, session.Owner.Id);
    }

    private sealed class Socket(CurrentUser user)
    {
        public CurrentUser User { get; } = user;
        public Channel<string> Out { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // only the latest list matters
            SingleReader = true,
        });
    }

    /// <summary>A session's list entry changed: its owner and the people it is shared with hear.</summary>
    public void SessionChanged(Guid sessionId, string ownerId)
    {
        lock (_gate)
        {
            _dirtySessions.Add(sessionId);
            _dirtyUsers.Add(ownerId);
        }
        Schedule();
    }

    /// <summary>These people's lists changed (say, a share naming them was revoked).</summary>
    public void PeopleChanged(IEnumerable<(string? UserId, string Upn)> people)
    {
        lock (_gate)
            foreach (var (userId, upn) in people)
            {
                if (userId is not null) _dirtyUsers.Add(userId);
                _dirtyUsers.Add(UpnKey(upn));
            }
        Schedule();
    }

    private static string UpnKey(string upn) => "upn:" + upn.ToLowerInvariant();

    /// <summary>
    /// A user opened or closed GreenGlow: the owners of sessions shared with them hear, for the
    /// lights in their sharing panels.
    /// </summary>
    private async Task PresenceChangedAsync(CurrentUser user)
    {
        try
        {
            foreach (var id in (await _shares.SharedWithAsync(user)).Keys)
                if (_sessions.Find(id) is { } session) SessionChanged(session.Id, session.Owner.Id);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning("could not push a presence change: {Error}", e.GetType().Name);
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_scheduled) return;
            _scheduled = true;
        }
        _ = Task.Delay(Batch, _clock).ContinueWith(_ => FlushAsync(), TaskScheduler.Default).Unwrap();
    }

    private async Task FlushAsync()
    {
        Guid[] sessionIds;
        HashSet<string> keys;
        lock (_gate)
        {
            _scheduled = false;
            sessionIds = [.. _dirtySessions];
            keys = [.. _dirtyUsers];
            _dirtySessions.Clear();
            _dirtyUsers.Clear();
        }
        try
        {
            foreach (var (userId, upn) in await _shares.PeopleOfAsync(sessionIds))
            {
                if (userId is not null) keys.Add(userId);
                keys.Add(UpnKey(upn));
            }
            List<Socket> targets;
            lock (_gate)
                targets = _sockets.Where(s => keys.Contains(s.User.Id) || keys.Contains(UpnKey(s.User.Upn))).ToList();
            foreach (var group in targets.GroupBy(s => s.User.Id))
            {
                var message = await MessageAsync(group.First().User);
                foreach (var socket in group) socket.Out.Writer.TryWrite(message);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The server is stopping, or the database is busy: the next change pushes again.
            _log.LogWarning("could not push session lists: {Error}", e.GetType().Name);
        }
    }

    private async Task<string> MessageAsync(CurrentUser user) =>
        JsonSerializer.Serialize(new { type = "sessions", sessions = await SessionEndpoints.ListAsync(user, _sessions, _shares) },
            HostSession.Json);

    /// <summary>/ws/user: the list at once, then again whenever it changes, until the page goes.</summary>
    public static async Task AttachAsync(HttpContext context, UserFeed feed, OriginPolicy origins)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!origins.IsAllowed(context.Request.Headers.Origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var user = CurrentUser.From(context.User)!;
        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });
        var socket = new Socket(user);
        lock (feed._gate) feed._sockets.Add(socket);
        feed._presence.Enter(user.Upn);
        await feed.PresenceChangedAsync(user);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            socket.Out.Writer.TryWrite(await feed.MessageAsync(user));
            var sending = SendAsync(ws, socket, stop.Token);
            await ReceiveAsync(ws, stop.Token);
            await stop.CancelAsync();
            await sending;
        }
        finally
        {
            lock (feed._gate) feed._sockets.Remove(socket);
            feed._presence.Leave(user.Upn);
            await feed.PresenceChangedAsync(user);
        }
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Gone already.
            }
        }
    }

    private static async Task SendAsync(WebSocket ws, Socket socket, CancellationToken cancel)
    {
        try
        {
            await foreach (var message in socket.Out.Reader.ReadAllAsync(cancel))
                await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // The page went.
        }
    }

    /// <summary>The page sends nothing; this waits for it to close.</summary>
    private static async Task ReceiveAsync(WebSocket ws, CancellationToken cancel)
    {
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, cancel);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // Gone.
        }
    }
}
