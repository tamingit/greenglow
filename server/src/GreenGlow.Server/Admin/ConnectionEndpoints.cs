using System.Security.Cryptography.X509Certificates;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/connections and /api/admin/settings: the IBM i systems users may connect to,
/// who may use each, and the session limits. Administrators only.
/// </summary>
public static class ConnectionEndpoints
{
    public sealed record ConnectionRequest(
        string? Name,
        string? Label,
        string? Url,
        int? MaxSessionsPerUser,
        bool? IsTest,
        bool? Enabled,
        int? SortOrder,
        string? Color = null);

    public sealed record GrantRequest(string? Type, string? Id, string? Name);

    public sealed record SettingRequest(int? Value);

    public static void MapConnectionEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/connections", async (IDbContextFactory<GreenGlowDb> dbs, SessionManager sessions, Relay.HostConnector connector) =>
        {
            await using var db = await dbs.CreateDbContextAsync();
            var rows = await db.Connections.AsNoTracking().Include(c => c.Grants)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
            var live = sessions.All.GroupBy(s => s.Target.Name).ToDictionary(g => g.Key, g => g.Count());
            return Results.Json(new
            {
                certificatesVerified = connector.Verifies,
                connections = rows.Select(c => Describe(c, live.GetValueOrDefault(c.Name), connector.Verifies)),
            });
        });

        admin.MapPost("/connections", async (ConnectionRequest request, IDbContextFactory<GreenGlowDb> dbs, TimeProvider clock,
            Relay.HostConnector connector) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (!TargetParser.IsValidName(name))
                return Problem("name", "The name must start with a letter and use lowercase letters, digits and dashes (up to 32).");
            await using var db = await dbs.CreateDbContextAsync();
            if (await db.Connections.AnyAsync(c => c.Name == name))
                return Results.Conflict(new { error = "name-taken", message = $"There is already a connection called {name}." });
            var connection = new HostConnection { Name = name, Label = name, Host = "", CreatedAt = clock.GetUtcNow() };
            if (Apply(connection, request with { Url = request.Url ?? "" }) is { } problem) return problem;
            connection.SortOrder = request.SortOrder ?? (await db.Connections.Select(c => (int?)c.SortOrder).MaxAsync() ?? -1) + 1;
            connection.UpdatedAt = clock.GetUtcNow();
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
            return Results.Json(Describe(connection, 0, connector.Verifies), statusCode: StatusCodes.Status201Created);
        });

        admin.MapPut("/connections/{id:guid}", async (Guid id, ConnectionRequest request, IDbContextFactory<GreenGlowDb> dbs,
            SessionManager sessions, TimeProvider clock, Relay.HostConnector connector) =>
        {
            await using var db = await dbs.CreateDbContextAsync();
            var connection = await db.Connections.Include(c => c.Grants).FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();
            if (request.Name is not null && request.Name.Trim() != connection.Name)
                return Problem("name", "A connection's name can't change: sessions and the log refer to it. Add a new one instead.");
            if (Apply(connection, request) is { } problem) return problem;
            connection.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Json(Describe(connection, sessions.All.Count(s => s.Target.Name == connection.Name), connector.Verifies));
        });

        admin.MapDelete("/connections/{id:guid}", async (Guid id, IDbContextFactory<GreenGlowDb> dbs) =>
        {
            // Sessions already open on it carry on until they end; no new ones can start. Rows are
            // deleted one by one (not in bulk) so each delete reaches the other nodes.
            await using var db = await dbs.CreateDbContextAsync();
            var connection = await db.Connections.Include(c => c.Grants).FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();
            db.UserColors.RemoveRange(await db.UserColors.Where(c => c.ConnectionId == id).ToListAsync());
            db.Connections.Remove(connection);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/connections/{id:guid}/grants", async (Guid id, GrantRequest request, IDbContextFactory<GreenGlowDb> dbs) =>
        {
            var type = request.Type?.Trim().ToLowerInvariant() switch
            {
                "user" => PrincipalType.User,
                "group" => PrincipalType.Group,
                _ => (PrincipalType?)null,
            };
            if (type is null) return Problem("type", "Access is given to a user or a group.");
            if (!Guid.TryParse(request.Id, out var principal)) return Problem("id", "Pick the user or group from the directory.");
            await using var db = await dbs.CreateDbContextAsync();
            if (!await db.Connections.AnyAsync(c => c.Id == id)) return Results.NotFound();
            var existing = await db.Grants.FirstOrDefaultAsync(g =>
                g.ConnectionId == id && g.PrincipalType == type && g.PrincipalId == principal.ToString());
            if (existing is not null) return Results.Json(DescribeGrant(existing));
            var grant = new ConnectionGrant
            {
                ConnectionId = id,
                PrincipalType = type.Value,
                PrincipalId = principal.ToString(),
                DisplayName = (request.Name ?? "").Trim() is { Length: > 0 and <= 256 } name ? name : principal.ToString(),
            };
            db.Grants.Add(grant);
            await db.SaveChangesAsync();
            return Results.Json(DescribeGrant(grant), statusCode: StatusCodes.Status201Created);
        });

        admin.MapCertificateEndpoints();

        admin.MapDelete("/connections/{id:guid}/grants/{grantId:guid}", async (Guid id, Guid grantId, IDbContextFactory<GreenGlowDb> dbs) =>
        {
            await using var db = await dbs.CreateDbContextAsync();
            var grant = await db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.ConnectionId == id);
            if (grant is null) return Results.NotFound();
            db.Grants.Remove(grant);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapGet("/settings", (SettingsStore settings) => Results.Json(new
        {
            settings = settings.All().Select(s => new
            {
                key = s.Key,
                value = s.Value,
                isDefault = s.IsDefault,
                min = SettingsStore.Ranges[s.Key].Min,
                max = SettingsStore.Ranges[s.Key].Max,
            }),
        }));

        // A null value goes back to the configured default.
        admin.MapPut("/settings/{key}", async (string key, SettingRequest request, SettingsStore settings) =>
        {
            if (!SettingsStore.Ranges.TryGetValue(key, out var range)) return Results.NotFound();
            if (request.Value is { } v && (v < range.Min || v > range.Max))
                return Problem("value", $"The value must be from {range.Min} to {range.Max}.");
            await settings.SetAsync(key, request.Value);
            return Results.NoContent();
        });
    }

    /// <summary>Applies the fields a request gives. Returns a problem response if one is invalid.</summary>
    private static IResult? Apply(HostConnection connection, ConnectionRequest request)
    {
        if (request.Label is not null)
        {
            try
            {
                connection.Label = request.Label.Trim() is { Length: > 0 } label
                    ? TargetParser.ValidLabel(connection.Name, label)
                    : connection.Name;
            }
            catch (ConfigException e)
            {
                return Problem("label", e.Message);
            }
        }
        if (request.Url is not null)
        {
            try
            {
                var target = TargetParser.ParseUrl(connection.Name, connection.Label, request.Url.Trim());
                (connection.Host, connection.Port, connection.Secure) = (target.Host, target.Port, target.Secure);
            }
            catch (ConfigException e)
            {
                return Problem("url", e.Message);
            }
        }
        if (request.MaxSessionsPerUser is { } max)
        {
            if (max is < 1 or > ConnectionSeed.MaxSessionsLimit)
                return Problem("maxSessionsPerUser", $"Sessions per user must be from 1 to {ConnectionSeed.MaxSessionsLimit}.");
            connection.MaxSessionsPerUser = max;
        }
        if (request.IsTest is { } isTest) connection.IsTest = isTest;
        if (request.Enabled is { } enabled) connection.Enabled = enabled;
        if (request.SortOrder is { } order) connection.SortOrder = order;
        if (request.Color is { } color)
        {
            // An empty colour takes it away.
            if (color.Trim() is { Length: > 0 } given && SystemColor.Parse(given) is null)
                return Problem("color", "The colour must be #rrggbb.");
            connection.Color = SystemColor.Parse(color);
        }
        return null;
    }

    private static IResult Problem(string field, string message) =>
        Results.BadRequest(new { error = field, message });

    /// <summary>
    /// For administrators: the host is shown here, never to other users. `serverCa`: the server
    /// has a CA of its own, which verifies every tls:// connection.
    /// </summary>
    internal static object Describe(HostConnection c, int liveSessions, bool serverCa) => new
    {
        id = c.Id,
        name = c.Name,
        label = c.Label,
        url = c.Url,
        secure = c.Secure,
        maxSessionsPerUser = c.MaxSessionsPerUser,
        isTest = c.IsTest,
        enabled = c.Enabled,
        sortOrder = c.SortOrder,
        color = c.Color,
        // tls://: whether its certificate is checked, and the CA trusted for it alone, if any.
        verified = c.Secure && (serverCa || c.TrustedCaPem is not null),
        trust = c.TrustedCaPem is null ? null : DescribeTrust(c),
        liveSessions,
        grants = c.Grants.OrderBy(g => g.PrincipalType).ThenBy(g => g.DisplayName).Select(DescribeGrant),
    };

    private static object DescribeTrust(HostConnection c)
    {
        using var ca = X509Certificate2.CreateFromPem(c.TrustedCaPem);
        return new
        {
            subject = ca.Subject,
            sha256 = CertificateEndpoints.Fingerprint(ca),
            notAfter = new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            name = c.TrustedName,
            by = c.TrustedBy,
            at = c.TrustedAt,
        };
    }

    private static object DescribeGrant(ConnectionGrant g) => new
    {
        id = g.Id,
        type = g.PrincipalType == PrincipalType.User ? "user" : "group",
        principalId = g.PrincipalId,
        name = g.DisplayName,
    };
}
