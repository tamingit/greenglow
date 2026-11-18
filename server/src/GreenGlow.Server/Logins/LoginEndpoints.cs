using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.People;
using GreenGlow.Server.Relay;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Logins;

/// <summary>
/// /api/logins/{connection}: the IBM i logins a user keeps for a system they may use. A
/// login is saved only after the IBM i's sign-on server accepted its password (once: a
/// refused one counts toward the profile's QMAXSIGN), and no answer ever carries a password.
/// The same access as sessions: a system the user may not use answers like one that doesn't
/// exist.
/// </summary>
public static class LoginEndpoints
{
    public sealed record SaveRequest(string? User, string? Password, bool? Remember);

    public sealed record ChangeRequest(string? User, string? Password, string? NewPassword, bool? Remember);

    public static void MapLoginEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/logins/{connection}");

        group.MapGet("", async (string connection, HttpContext context, Access access, IbmLogins logins) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is not { } target) return NoSystem();
            return Results.Json(new
            {
                // Whether "remember the password" may be offered: the server has a key for it, and no administrator forbade it.
                canRemember = logins.CanRemember,
                logins = (await logins.ListAsync(user.Id, target)).Select(Describe),
            });
        });

        // A new login, or a new password for one the user has: checked with the IBM i first.
        group.MapPost("", async (string connection, SaveRequest request, HttpContext context, Access access, IbmLogins logins,
            HostEndpoints endpoints, HostConnector connector, ConnectionLogger log, TimeProvider clock) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is not { } target) return NoSystem();
            return await CheckedAsync(context, user, target, request.User, request.Password, null, request.Remember,
                logins, endpoints, connector, log, clock);
        }).RequireRateLimiting(RateLimiting.SignOn);

        // A new IBM i password for the user profile, given the current one (which may have
        // expired); the login is saved with the new one.
        group.MapPost("/change", async (string connection, ChangeRequest request, HttpContext context, Access access, IbmLogins logins,
            HostEndpoints endpoints, HostConnector connector, ConnectionLogger log, TimeProvider clock) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is not { } target) return NoSystem();
            if (string.IsNullOrEmpty(request.NewPassword))
                return Results.BadRequest(new { error = "new-password-refused", message = "Type the new password." });
            return await CheckedAsync(context, user, target, request.User, request.Password, request.NewPassword, request.Remember,
                logins, endpoints, connector, log, clock);
        }).RequireRateLimiting(RateLimiting.SignOn);

        group.MapDelete("/{id:guid}", async (string connection, Guid id, HttpContext context, Access access, IbmLogins logins) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is not { } target) return NoSystem();
            return await logins.RemoveAsync(user.Id, target, id) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static async Task<IResult> CheckedAsync(HttpContext context, CurrentUser user, Target target, string? ibmUser,
        string? password, string? newPassword, bool? remember, IbmLogins logins, HostEndpoints endpoints, HostConnector connector,
        ConnectionLogger log, TimeProvider clock)
    {
        var name = (ibmUser ?? "").Trim().ToUpperInvariant();
        async Task LogAsync(string outcome)
        {
            var now = clock.GetUtcNow();
            await log.AddAsync(new ConnectionLog
            {
                Kind = LogKind.SignIn,
                SessionId = Guid.NewGuid(),
                UserId = user.Id,
                Upn = user.Upn,
                ClientIp = ClientAddress.Of(context),
                ConnectionName = target.Name,
                IbmUser = name.Length is > 0 and <= 10 ? name : null,
                StartedAt = now,
                EndedAt = now,
                EndReason = outcome,
            });
        }

        try
        {
            var endpoint = await endpoints.ForAsync(target, HostService.SignOn, context.RequestAborted);
            if (newPassword is not null)
            {
                await SignOnServer.ChangePasswordAsync(connector, endpoint, name, password ?? "", newPassword, context.RequestAborted);
                await LogAsync("IBM i password changed");
                password = newPassword;
            }
            var info = await SignOnServer.VerifyAsync(connector, endpoint, name, password ?? "", context.RequestAborted);
            var saved = await logins.SaveAsync(user.Id, target, info.User, password!, remember);
            await LogAsync("login saved");
            return Results.Json(Describe(saved), statusCode: StatusCodes.Status201Created);
        }
        catch (SignOnException e)
        {
            if (e.Failure == SignOnFailure.Unreachable) endpoints.Forget(target, HostService.SignOn);
            // Typed wrong before anything reached the IBM i: not worth a log row.
            var local = e.Code == 0 && e.Failure is SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword or SignOnFailure.NewPasswordRefused;
            if (!local || e.Failure is SignOnFailure.Unreachable or SignOnFailure.Protocol)
                await LogAsync($"login check failed: {PasswordLogin.Words(e.Failure)}");
            // The IBM i no longer takes what is kept for this login.
            if (e.Failure is SignOnFailure.WrongPassword or SignOnFailure.WrongPasswordDisabled or SignOnFailure.PasswordExpired
                && name.Length is > 0 and <= 10)
                await logins.RefusedAsync(user.Id, target, name);
            return Failure(e);
        }
    }

    /// <summary>A sign-on failure as an answer: 400 for what was typed wrong, 502 when the IBM i can't be reached, else 422.</summary>
    public static IResult Failure(SignOnException e)
    {
        var status = e.Failure switch
        {
            SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword when e.Code == 0 => StatusCodes.Status400BadRequest,
            SignOnFailure.Unreachable or SignOnFailure.Protocol => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Results.Json(new { error = Kebab(e.Failure), message = e.Message }, statusCode: status);
    }

    public static string Kebab(SignOnFailure failure) =>
        System.Text.RegularExpressions.Regex.Replace(failure.ToString(), "(?<=[a-z])(?=[A-Z])", "-").ToLowerInvariant();

    public static object Describe(LoginInfo login) => new
    {
        id = login.Id,
        ibmUser = login.IbmUser,
        remembered = login.Remembered,
        // The password is to hand: a session or the printer panel can sign on with this login now.
        ready = login.Ready,
    };

    private static IResult NoSystem() => Results.NotFound(new { error = "unknown-target", message = "No such system." });
}
