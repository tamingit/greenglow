using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GreenGlow.Engine;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using GreenGlow.Server.Relay;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Sessions;

/// <summary>
/// /api/sessions to open, list and end host sessions, and /ws/session/{id} to attach a browser.
/// The browser gets screens as JSON and sends keys as JSON; it never sees host bytes.
/// </summary>
public static class SessionEndpoints
{
    /// <summary>Room for a paste of Paste.MaxCharacters, escaped as JSON.</summary>
    internal const int MaxClientMessage = 32 * 1024;

    /// <summary>The reason the page closes its socket with when the user disconnects or closes the tab.</summary>
    internal const string UserDisconnected = "user disconnected";

    /// <summary>How long a browser has to answer the server's close frame.</summary>
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    public sealed record OpenRequest(string Target, bool Spare = false);

    public static void MapSessionEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        // The user's own sessions, then the live ones shared with them while their owner is in them.
        api.MapGet("/sessions", async (HttpContext context, SessionManager sessions, Shares shares, Presence presence) =>
        {
            var user = CurrentUser.From(context.User)!;
            presence.Seen(user.Upn);
            return Results.Json(new { sessions = await ListAsync(user, sessions, shares) });
        });

        api.MapPost("/sessions", async (OpenRequest request, HttpContext context, Access access, SessionManager sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            // A system this user may not use answers like one that doesn't exist.
            var target = await access.FindAsync(user, request.Target ?? "");
            if (target is null) return Results.NotFound(new { error = "unknown-target", message = "No such system." });
            try
            {
                var session = await sessions.CreateAsync(user, target, ClientAddress.Of(context), request.Spare);
                return Results.Json(Describe(session, FrontendRole.Owner), statusCode: StatusCodes.Status201Created);
            }
            catch (SessionRefusedException e)
            {
                // A draining node is about to restart: another one can open the session.
                return Results.Json(new { error = e.Code, message = e.Message },
                    statusCode: e.Code == SessionManager.Draining ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict);
            }
        });

        api.MapDelete("/sessions/{id:guid}", (Guid id, HttpContext context, SessionManager sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null || session.Owner.Id != user.Id) return Results.NotFound();
            session.Close("ended by the user");
            return Results.NoContent();
        });

        app.Map("/ws/session/{id:guid}", AttachAsync).RequireAuthorization();
    }

    /// <summary>
    /// A user's session list: their own sessions, then the live ones shared with them while
    /// their owner is in them. GET /api/sessions answers with it and /ws/user pushes it.
    /// </summary>
    public static async Task<List<object>> ListAsync(CurrentUser user, SessionManager sessions, Shares shares)
    {
        var shared = (await shares.SharedWithAsync(user))
            .Select(p => (Session: sessions.Find(p.Key), p.Value.Role, p.Value.Token))
            .Where(p => p.Session is not null && p.Session.Owner.Id != user.Id && p.Session.OwnerPresent)
            .OrderBy(p => p.Session!.CreatedAt)
            .Select(p => Describe(p.Session!, p.Role, p.Token));
        return [.. sessions.OwnedBy(user.Id).Select(s => Describe(s, FrontendRole.Owner)), .. shared];
    }

    /// <summary>What the session list shows; never the host name.</summary>
    /// <param name="shareToken">For a shared session: the link's token, which the recipient attaches with.</param>
    public static object Describe(HostSession s, FrontendRole role, string? shareToken = null) => new
    {
        id = s.Id,
        target = s.Target.Name,
        label = s.Target.Label,
        secure = s.Target.Secure,
        createdAt = s.CreatedAt,
        device = s.DeviceName,
        ibmUser = s.IbmUser,
        title = s.Title,
        frontends = s.FrontendCount,
        ownerFrontends = s.OwnerFrontendCount,
        ownerPresent = s.OwnerPresent,
        idleSince = s.IdleSince,
        signedOn = s.SignedOn,
        owner = s.Owner.Upn,
        role = HostSession.RoleName(role),
        shareToken,
    };

    private static async Task AttachAsync(HttpContext context, Guid id, SessionManager sessions, OriginPolicy origins,
        ConnectionLogger connectionLog, SettingsStore settings, Shares shares, DemoOptions demo)
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
        var session = sessions.Find(id);
        // The owner, or someone named in a live share link for this session (?share=<token>).
        var (role, shareId) = (FrontendRole.Owner, (Guid?)null);
        // Demo guests never see one another's sessions: there is no sharing in demo mode.
        if (session is not null && session.Owner.Id != user.Id && demo.Enabled) session = null;
        if (session is not null && session.Owner.Id != user.Id)
        {
            var grant = await shares.ResolveAsync(context.Request.Query["share"].ToString(), user);
            if (grant is null || grant.SessionId != id) session = null;
            else (role, shareId) = (grant.Role, grant.ShareId);
        }
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The owner may take the session over from their own browsers elsewhere (?takeover=1).
        var takeOver = role == FrontendRole.Owner && context.Request.Query["takeover"] == "1";
        // Only the owner's own browsers count; people it is shared with don't. Checked again on
        // the session's loop, where two browsers attaching at once can't both get in.
        var limit = settings.MaxFrontendsPerSession;
        if (!takeOver && role == FrontendRole.Owner && session.OwnerFrontendCount >= limit)
        {
            await Results.Conflict(new { error = "session-full", message = FullMessage(limit) }).ExecuteAsync(context);
            return;
        }

        var ip = ClientAddress.Of(context);
        var frontend = new Frontend(user, role, ip, shareId);
        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });
        switch (await session.AttachAsync(frontend, limit, takeOver))
        {
            case AttachResult.Ended:
                await CloseQuietly(ws, WebSocketCloseStatus.NormalClosure, "session ended");
                return;
            case AttachResult.Full:
                await SendAsync(ws, JsonSerializer.Serialize(new { type = "closed", reason = FullMessage(limit) }, HostSession.Json));
                await CloseQuietly(ws, WebSocketCloseStatus.PolicyViolation, "session full");
                return;
            case AttachResult.OwnerAway:
                await SendAsync(ws, JsonSerializer.Serialize(new { type = "closed", reason = HostSession.OwnerAwayReason }, HostSession.Json));
                await CloseQuietly(ws, WebSocketCloseStatus.NormalClosure, "owner away");
                return;
        }
        var logId = await connectionLog.StartAsync(new ConnectionLog
        {
            Kind = LogKind.Frontend,
            SessionId = session.Id,
            UserId = user.Id,
            Upn = user.Upn,
            ClientIp = ip,
            ConnectionName = session.Target.Name,
            DeviceName = session.DeviceName,
            Role = HostSession.RoleName(frontend.Role),
        });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var sending = SendLoopAsync(ws, frontend, stop);
        var reason = await ReceiveLoopAsync(ws, session, frontend, stop.Token);
        session.Detach(frontend, disconnected: reason == UserDisconnected);
        await stop.CancelAsync();
        var sent = await sending;
        await connectionLog.EndAsync(logId, sent ?? reason);
        await CloseQuietly(ws, WebSocketCloseStatus.NormalClosure, sent ?? reason);
    }

    /// <summary>Sends until the outbox closes. Returns a close reason when the session ended it.</summary>
    private static async Task<string?> SendLoopAsync(WebSocket ws, Frontend frontend, CancellationTokenSource stop)
    {
        var cancel = stop.Token;
        try
        {
            while (await frontend.Outbox.NextAsync(cancel) is { } message)
                await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel);
            var reason = frontend.Outbox.Overflowed ? "browser too slow" : "session ended";
            await CloseQuietly(ws, frontend.Outbox.Overflowed ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.NormalClosure, reason);
            // The session is over. Reading goes on until the browser answers the close frame:
            // cancelling the read aborts the socket, and a reset can reach the browser before it
            // has read the last message and the close frame, which it then loses (1006, no
            // reason). A browser that never answers is cut off after a while, so it doesn't keep
            // this attachment (and its log row) open.
            stop.CancelAfter(CloseHandshakeTimeout);
            return reason;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            return null;
        }
    }

    private static async Task<string> ReceiveLoopAsync(WebSocket ws, HostSession session, Frontend frontend, CancellationToken cancel)
    {
        var buffer = new byte[MaxClientMessage];
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length) return "message too big";
                    result = await ws.ReceiveAsync(buffer.AsMemory(length), cancel);
                    length += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
                if (result.MessageType == WebSocketMessageType.Close)
                    return ws.CloseStatusDescription == UserDisconnected ? UserDisconnected : "browser closed";
                if (result.MessageType != WebSocketMessageType.Text) return "binary frames are not accepted";
                if (!Handle(buffer.AsSpan(0, length), session, frontend)) return "bad message";
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            return "browser gone";
        }
    }

    /// <summary>One message from the browser: a key, or taking the keyboard.</summary>
    internal static bool Handle(ReadOnlySpan<byte> json, HostSession session, Frontend frontend)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(json.ToArray()).RootElement;
        }
        catch (JsonException)
        {
            return false;
        }
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            return false;
        switch (type.GetString())
        {
            case "key":
                if (!message.TryGetProperty("key", out var key) || ParseKey(key) is not { } parsed) return false;
                session.Key(frontend, parsed);
                return true;
            case "paste":
                if (!message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return false;
                var pasted = text.GetString()!;
                if (pasted.Length > Paste.MaxCharacters) return false;
                session.PasteText(frontend, pasted);
                return true;
            case "take-control":
                session.TakeControl(frontend);
                return true;
            case "return-control":
                session.ReturnControl(frontend);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A key from untrusted JSON: only names the engine knows, one character, or a cell.</summary>
    internal static Key? ParseKey(JsonElement key)
    {
        if (key.ValueKind != JsonValueKind.Object || !key.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            return null;
        switch (type.GetString())
        {
            case "character":
                var text = key.TryGetProperty("char", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return text is { Length: > 0 } && text.EnumerateRunes().Count() == 1 && !char.IsControl(text, 0)
                    ? new CharacterKey(text)
                    : null;
            case "function":
                var name = key.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                return name is not null && Keyboard.IsKeyName(name) ? new FunctionKey(name) : null;
            case "cursor":
                return key.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var row)
                       && row is >= 1 and <= 28
                       && key.TryGetProperty("column", out var col) && col.ValueKind == JsonValueKind.Number
                       && col.TryGetInt32(out var column) && column is >= 1 and <= 132
                    ? new CursorKey(row, column)
                    : null;
            default:
                return null;
        }
    }

    private static string FullMessage(int limit) =>
        $"This session is already open in {limit} of your browsers, the most allowed.";

    private static async Task SendAsync(WebSocket ws, string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // The browser is gone.
        }
    }

    private static async Task CloseQuietly(WebSocket ws, WebSocketCloseStatus code, string reason)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseOutputAsync(code, reason, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // The browser is gone.
        }
    }
}
