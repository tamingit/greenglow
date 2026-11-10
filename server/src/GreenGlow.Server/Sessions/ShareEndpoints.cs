using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;

namespace GreenGlow.Server.Sessions;

/// <summary>
/// /api/sessions/{id}/shares for the owner to share a session and revoke shares, and
/// /api/shares/{token} for a recipient to see what a link opens.
/// </summary>
public static class ShareEndpoints
{
    public sealed record RecipientRequest(string? Upn, string? Access);

    public sealed record ShareRequest(List<RecipientRequest>? Recipients);

    public static void MapShareEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/sessions/{id:guid}/shares", async (Guid id, ShareRequest request, HttpContext context,
            SessionManager sessions, Shares shares, ServerOptions server, Presence presence, UserFeed feed) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null || session.Owner.Id != user.Id) return Results.NotFound();
            var recipients = new List<Shares.Recipient>();
            foreach (var r in request.Recipients ?? [])
            {
                var access = ParseAccess(r.Access);
                if (access is null)
                    return Results.BadRequest(new { error = "access", message = "Access is view (view-only) or full (full access)." });
                recipients.Add(new Shares.Recipient(r.Upn ?? "", access.Value));
            }
            try
            {
                var share = await shares.CreateAsync(session, user, recipients);
                // The people named see it in their lists at once.
                feed.SessionChanged(session.Id, user.Id);
                return Results.Json(Describe(share, server, session, presence), statusCode: StatusCodes.Status201Created);
            }
            catch (Shares.ShareException e)
            {
                return Results.BadRequest(new { error = e.Code, message = e.Message });
            }
        });

        api.MapGet("/sessions/{id:guid}/shares", async (Guid id, HttpContext context, SessionManager sessions,
            Shares shares, ServerOptions server, Presence presence) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null || session.Owner.Id != user.Id) return Results.NotFound();
            return Results.Json(new { shares = (await shares.ForSessionAsync(id)).Select(s => Describe(s, server, session, presence)) });
        });

        // Who a session is shared with, for anyone in it: its owner and the people named on its
        // live shares, with their access, whether they are watching or have GreenGlow open, and
        // who has the keyboard. Names only: no links, tokens or IDs.
        api.MapGet("/sessions/{id:guid}/people", async (Guid id, HttpContext context, SessionManager sessions,
            Shares shares, Presence presence) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null
                || (session.Owner.Id != user.Id && !(await shares.SharedWithAsync(user)).ContainsKey(id)))
                return Results.NotFound();
            var attached = session.Attached;
            var keyboard = session.KeyboardHolder;
            var owner = session.Owner;
            var people = (await shares.ForSessionAsync(id))
                .SelectMany(s => s.Recipients.Select(r => (Share: s, Person: r)))
                .GroupBy(x => x.Person.Upn.ToLowerInvariant())
                .Select(g =>
                {
                    var watching = g.Any(x => attached.Any(a => a.ShareId == x.Share.Id && a.UserId == x.Person.UserId));
                    return new
                    {
                        upn = g.First().Person.Upn,
                        name = g.First().Person.DisplayName is { Length: > 0 } n ? n : g.First().Person.Upn,
                        access = g.Any(x => x.Person.Access == ShareAccess.FullAccess) ? "full" : "view",
                        watching,
                        keyboard = g.Any(x => keyboard is { } k && k.ShareId == x.Share.Id && k.UserId == x.Person.UserId),
                        inGreenGlow = watching || presence.IsIn(g.First().Person.Upn),
                        you = string.Equals(g.First().Person.Upn, user.Upn, StringComparison.OrdinalIgnoreCase),
                    };
                })
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var ownerWatching = attached.Any(a => a.UserId == owner.Id && a.ShareId is null);
            return Results.Json(new
            {
                owner = new
                {
                    upn = owner.Upn,
                    name = owner.Name is { Length: > 0 } n ? n : owner.Upn,
                    watching = ownerWatching,
                    keyboard = keyboard is { } k && k.UserId == owner.Id && k.ShareId is null,
                    inGreenGlow = ownerWatching || presence.IsIn(owner.Upn),
                    you = owner.Id == user.Id,
                },
                people,
            });
        });

        // One person on a share: change their access (view or full), at once in their browser.
        api.MapPatch("/sessions/{id:guid}/shares/{shareId:guid}/recipients/{recipientId:guid}", async (Guid id, Guid shareId,
            Guid recipientId, RecipientRequest request, HttpContext context, SessionManager sessions, Shares shares,
            UserFeed feed) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null || session.Owner.Id != user.Id) return Results.NotFound();
            if (ParseAccess(request.Access) is not { } access)
                return Results.BadRequest(new { error = "access", message = "Access is view (view-only) or full (full access)." });
            var recipient = await shares.ChangeAccessAsync(id, shareId, recipientId, access);
            if (recipient is null) return Results.NotFound();
            if (recipient.UserId is { } userId)
                session.SetShareRole(shareId, userId, access == ShareAccess.FullAccess ? FrontendRole.Full : FrontendRole.View);
            feed.SessionChanged(session.Id, user.Id);
            return Results.NoContent();
        });

        // One person off a share: their browsers leave at once; a share left empty is revoked.
        api.MapDelete("/sessions/{id:guid}/shares/{shareId:guid}/recipients/{recipientId:guid}", async (Guid id, Guid shareId,
            Guid recipientId, HttpContext context, SessionManager sessions, Shares shares, UserFeed feed) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null || session.Owner.Id != user.Id) return Results.NotFound();
            if (await shares.RemoveAsync(id, shareId, recipientId) is not { } removed) return Results.NotFound();
            if (removed.Recipient.UserId is { } userId) session.DetachSharedUser(shareId, userId);
            // The person removed is no longer on the share, so they are told by name.
            feed.PeopleChanged([(removed.Recipient.UserId, removed.Recipient.Upn)]);
            feed.SessionChanged(session.Id, user.Id);
            return Results.NoContent();
        });

        api.MapDelete("/sessions/{id:guid}/shares/{shareId:guid}", async (Guid id, Guid shareId, HttpContext context,
            SessionManager sessions, Shares shares, UserFeed feed) =>
        {
            var user = CurrentUser.From(context.User)!;
            var session = sessions.Find(id);
            if (session is null || session.Owner.Id != user.Id) return Results.NotFound();
            var people = (await shares.ForSessionAsync(id)).Where(s => s.Id == shareId)
                .SelectMany(s => s.Recipients).Select(r => (r.UserId, r.Upn)).ToList();
            if (!await shares.RevokeAsync(id, shareId)) return Results.NotFound();
            feed.PeopleChanged(people);
            feed.SessionChanged(session.Id, user.Id);
            // Browsers that came in through it leave at once.
            session.DetachShare(shareId);
            return Results.NoContent();
        });

        // Someone a session is shared with takes it off their list: they leave every live share of
        // it that names them, and their browsers on it leave at once.
        api.MapDelete("/sessions/{id:guid}/shares/me", async (Guid id, HttpContext context, SessionManager sessions,
            Shares shares, UserFeed feed) =>
        {
            var user = CurrentUser.From(context.User)!;
            var left = await shares.LeaveAsync(id, user);
            if (left.Count == 0) return Results.NotFound();
            if (sessions.Find(id) is { } session)
            {
                foreach (var shareId in left) session.DetachSharedUser(shareId, user.Id);
                feed.SessionChanged(session.Id, session.Owner.Id);
            }
            feed.PeopleChanged([(user.Id, user.Upn)]);
            return Results.NoContent();
        });

        api.MapGet("/shares/{token}", async (string token, HttpContext context, Shares shares, SessionManager sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            var grant = await shares.ResolveAsync(token, user);
            var session = grant is null ? null : sessions.Find(grant.SessionId);
            // The same answer for a wrong token, someone not named, and an ended session.
            if (grant is null || session is null)
                return Results.NotFound(new { error = "share", message = "This link isn't for you, or its session has ended." });
            if (!session.OwnerPresent)
                return Results.Conflict(new { error = "owner-away", message = HostSession.OwnerAwayReason });
            return Results.Json(SessionEndpoints.Describe(session, grant.Role));
        });
    }

    private static ShareAccess? ParseAccess(string? access) => access?.Trim().ToLowerInvariant() switch
    {
        "view" or "view-only" => ShareAccess.ViewOnly,
        "full" or "full-access" => ShareAccess.FullAccess,
        _ => null,
    };

    /// <summary>
    /// A share for its owner: each person, their access, whether they're watching now, whether
    /// they have the keyboard, and whether they have GreenGlow open at all.
    /// </summary>
    private static object Describe(SessionShare share, ServerOptions server, HostSession session, Presence presence)
    {
        var attached = session.Attached;
        var keyboard = session.KeyboardHolder;
        return new
        {
            id = share.Id,
            createdAt = share.CreatedAt,
            url = $"{server.PublicUrl.TrimEnd('/')}/?share={Uri.EscapeDataString(share.Token)}",
            recipients = share.Recipients.OrderBy(r => r.Upn).Select(r => new
            {
                id = r.Id,
                upn = r.Upn,
                name = r.DisplayName,
                access = r.Access == ShareAccess.FullAccess ? "full" : "view",
                watching = attached.Any(a => a.ShareId == share.Id && a.UserId == r.UserId),
                keyboard = keyboard is { } k && k.ShareId == share.Id && k.UserId == r.UserId,
                inGreenGlow = presence.IsIn(r.Upn) || attached.Any(a => a.ShareId == share.Id && a.UserId == r.UserId),
            }),
        };
    }
}
