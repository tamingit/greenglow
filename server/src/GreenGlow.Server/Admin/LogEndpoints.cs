using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/log: the connection log. Each host session and each browser attached to one is a
/// row with who, from where, to which system and when; rows still open are current. Metadata
/// only: the log never holds screen contents or keystrokes.
/// </summary>
public static class LogEndpoints
{
    public const int MaxPage = 500;

    public static void MapLogEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/log", async (
            string? kind,
            string? q,
            string? connection,
            DateTimeOffset? from,
            DateTimeOffset? to,
            bool? current,
            int? limit,
            string? before,
            IDbContextFactory<GreenGlowDb> dbs,
            SessionManager sessions) =>
        {
            await using var db = await dbs.CreateDbContextAsync();
            var rows = db.Logs.AsNoTracking();
            if (kind is not null)
            {
                if (!Enum.TryParse<LogKind>(kind, ignoreCase: true, out var k))
                    return Results.BadRequest(new { error = "kind", message = "kind is session, frontend, certificate, printer or node." });
                rows = rows.Where(r => r.Kind == k);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = $"%{q.Trim().Replace("%", "").Replace("_", "")}%";
                rows = rows.Where(r => EF.Functions.Like(r.Upn ?? "", pattern) || EF.Functions.Like(r.ClientIp ?? "", pattern)
                                       || EF.Functions.Like(r.DeviceName ?? "", pattern) || EF.Functions.Like(r.IbmUser ?? "", pattern));
            }
            if (!string.IsNullOrWhiteSpace(connection)) rows = rows.Where(r => r.ConnectionName == connection.Trim());
            if (current == true) rows = rows.Where(r => r.EndedAt == null);
            // Paging back in time: `before` is where the last page stopped (UTC ticks of its oldest
            // start; rows from every node interleave by when they started).
            if (before is not null)
            {
                if (!long.TryParse(before, out var ticks) || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
                    return Results.BadRequest(new { error = "before", message = "before is the next value of a previous page." });
                var edge = new DateTimeOffset(ticks, TimeSpan.Zero);
                rows = rows.Where(r => r.StartedAt < edge);
            }
            // Rows overlapping the period: started before its end and not ended before its start.
            // Times are stored as UTC; compare in UTC.
            from = from?.ToUniversalTime();
            to = to?.ToUniversalTime();
            if (to is not null) rows = rows.Where(r => r.StartedAt <= to);
            if (from is not null) rows = rows.Where(r => r.EndedAt == null || r.EndedAt >= from);

            var take = Math.Clamp(limit ?? 100, 1, MaxPage);
            var page = await rows.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(take + 1).ToListAsync();
            var more = page.Count > take;
            if (more)
            {
                // The next page starts before this one's oldest start, so rows that started at
                // that same moment all go on this page, even past the limit.
                var oldest = page[take - 1].StartedAt;
                var tied = await rows.Where(r => r.StartedAt == oldest).ToListAsync();
                page = [.. page.Take(take).Where(r => r.StartedAt != oldest), .. tied.OrderByDescending(r => r.Id)];
            }

            var live = sessions.All.ToDictionary(s => s.Id);
            return Results.Json(new
            {
                rows = page.Select(r => new
                {
                    id = r.Id,
                    node = r.Node,
                    kind = r.Kind.ToString().ToLowerInvariant(),
                    sessionId = r.SessionId,
                    upn = r.Upn,
                    clientIp = r.ClientIp,
                    connection = r.ConnectionName,
                    device = r.DeviceName,
                    role = r.Role,
                    startedAt = r.StartedAt,
                    endedAt = r.EndedAt,
                    endReason = r.EndReason,
                    current = r.EndedAt is null,
                    // For a current host session, what it shows now.
                    // A print session keeps its IBM i user; a host session shows it while it's live.
                    ibmUser = r.Kind == LogKind.Printer ? r.IbmUser
                        : r.Kind == LogKind.Session && live.TryGetValue(r.SessionId, out var s) ? s.IbmUser : null,
                    filesOpened = r.FilesOpened,
                    title = r.Kind == LogKind.Session && live.TryGetValue(r.SessionId, out var t) ? t.Title : null,
                }),
                next = more ? page[^1].StartedAt.UtcTicks.ToString() : null,
            });
        });
    }
}
