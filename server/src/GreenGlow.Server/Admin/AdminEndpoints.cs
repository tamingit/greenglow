using GreenGlow.Server.Auth;
using GreenGlow.Server.People;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin: for administrators only. Administrators manage other administrators here;
/// connections, access and the connection log follow.
/// </summary>
public static class AdminEndpoints
{
    public sealed record AddAdminRequest(string Upn);

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(AuthSetup.AdminPolicy);

        admin.MapGet("/admins", async (UserStore users, AdminSeed seed) =>
        {
            var admins = await users.AdminsAsync();
            return Results.Json(new
            {
                admins = admins.Select(u => new
                {
                    id = u.Id,
                    upn = u.Upn,
                    name = u.DisplayName,
                    lastSeenAt = u.LastSeenAt == default ? (DateTimeOffset?)null : u.LastSeenAt,
                    initial = string.Equals(u.Upn, seed.Upn.Trim(), StringComparison.OrdinalIgnoreCase),
                }),
            });
        });

        admin.MapPost("/admins", async (AddAdminRequest request, UserStore users, IDirectory directory) =>
        {
            var upn = request.Upn?.Trim() ?? "";
            if (upn.Length == 0 || upn.Length > 256) return Results.BadRequest(new { error = "upn", message = "Enter a UPN." });
            var found = await directory.FindUserAsync(upn);
            if (found is null)
                return Results.NotFound(new { error = "not-found", message = $"No one in the directory has the UPN {upn}." });
            var user = await users.MakeAdminAsync(found.Id, found.Upn ?? upn, found.Name);
            return Results.Json(new { id = user.Id, upn = user.Upn, name = user.DisplayName }, statusCode: StatusCodes.Status201Created);
        });

        admin.MapDelete("/admins/{id}", async (string id, UserStore users) =>
            await users.RemoveAdminAsync(id)
                ? Results.NoContent()
                : Results.Conflict(new { error = "last-admin", message = "The last administrator can't be removed." }));

        admin.MapConnectionEndpoints();
        admin.MapLogEndpoints();
        admin.MapNodeEndpoints();

        admin.MapGet("/directory/groups", (string? q, IDirectory directory, ILogger<IDirectory> log) =>
                Directory(log, async () => Results.Json(new { groups = await directory.SearchGroupsAsync(q ?? "") })))
            .RequireRateLimiting(RateLimiting.Directory);

        return admin;
    }

    /// <summary>
    /// People in the directory, for any signed-in user (sharing a session or a macro needs it),
    /// and the groups the user is in (sharing a macro with a group); administrators see every
    /// group at /api/admin/directory/groups.
    /// </summary>
    public static void MapDirectoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/directory/users", (string? q, IDirectory directory, ILogger<IDirectory> log) =>
                Directory(log, async () =>
                    Results.Json(new { users = (q ?? "").Trim().Length < 3 ? [] : await directory.SearchUsersAsync(q!) })))
            .RequireRateLimiting(RateLimiting.Directory);
        api.MapGet("/directory/my-groups", (string? q, HttpContext context, IDirectory directory, Access access,
                ILogger<IDirectory> log) =>
                Directory(log, async () =>
                {
                    var user = CurrentUser.From(context.User)!;
                    var mine = await access.GroupsAsync(user.Id);
                    var found = await directory.SearchGroupsAsync(q ?? "");
                    return Results.Json(new { groups = found.Where(g => mine.Contains(g.Id)) });
                }))
            .RequireRateLimiting(RateLimiting.Directory);
    }

    /// <summary>A directory search, or 502 with the reason when Entra ID can't be asked.</summary>
    private static async Task<IResult> Directory(ILogger log, Func<Task<IResult>> search)
    {
        try
        {
            return await search();
        }
        catch (HttpRequestException e)
        {
            log.LogWarning("directory search failed: {Error}", e.Message);
            return Results.Json(new { error = "directory", message = $"Searching Entra ID failed. {e.Message}." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
