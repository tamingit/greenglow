using System.Security.Claims;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Logins;
using GreenGlow.Server.Relay;
using GreenGlow.Server.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GreenGlow.Server.Auth;

/// <summary>
/// Signing in with a password: a local account, an IBM i user profile (checked by the IBM
/// i's sign-on server, never here), or the breakglass administrator. Each try is limited per
/// client address and needs an allowed Origin, like every post; every try that got as far as
/// checking a password is a row in the connection log, with the name and the address, never
/// the password.
/// </summary>
public static class PasswordLogin
{
    public sealed record SignInRequest(string? User, string? Password, string? ReturnUrl);

    public sealed record ChangeRequest(string? User, string? Password, string? NewPassword, string? ReturnUrl);

    public sealed record BreakglassRequest(string? Password, string? ReturnUrl);

    public static void MapPasswordLogin(this WebApplication app)
    {
        // What the sign-in page shows. Anyone may ask: it says only which form to show.
        app.MapGet("/auth/method", async (SignInConfig config, TargetCatalog catalog, UserStore users) =>
        {
            var mode = config.Mode;
            var system = mode == SignInMode.IbmI && config.IbmConnection is { } name ? await catalog.FindAsync(name) : null;
            return Results.Json(new
            {
                method = Name(mode),
                // IBM i sign-in: the system whose user profiles sign in, as users know it.
                system = system?.Label,
                breakglass = (await users.FindAsync(AppUser.BreakglassId))?.PasswordHash is not null,
            });
        });

        app.MapPost("/auth/password", async (SignInRequest request, HttpContext context, SignInConfig config, Services services) =>
        {
            var name = (request.User ?? "").Trim();
            var password = request.Password ?? "";
            if (name.Length is 0 or > 256 || password.Length is 0 or > 256)
                return Refused(StatusCodes.Status400BadRequest, "missing", "Type your user name and password.");
            return config.Mode switch
            {
                SignInMode.Local => await LocalAsync(context, services, name, password, null, request.ReturnUrl),
                SignInMode.IbmI => await IbmAsync(context, config, services, name, password, null, request.ReturnUrl),
                _ => Results.NotFound(),
            };
        }).RequireRateLimiting(RateLimiting.Password);

        // A new password, given the current one: a local account's (asked for at sign-in when
        // an administrator set it), or an IBM i user profile's (also when it has expired).
        app.MapPost("/auth/password/change", async (ChangeRequest request, HttpContext context, SignInConfig config, Services services) =>
        {
            var name = (request.User ?? "").Trim();
            var password = request.Password ?? "";
            var next = request.NewPassword ?? "";
            if (name.Length is 0 or > 256 || password.Length is 0 or > 256 || next.Length is 0 or > 256)
                return Refused(StatusCodes.Status400BadRequest, "missing", "Type your user name, your password and the new password.");
            return config.Mode switch
            {
                SignInMode.Local => await LocalAsync(context, services, name, password, next, request.ReturnUrl),
                SignInMode.IbmI => await IbmAsync(context, config, services, name, password, next, request.ReturnUrl),
                _ => Results.NotFound(),
            };
        }).RequireRateLimiting(RateLimiting.Password);

        // The breakglass administrator: a way in when the sign-in method itself is broken.
        app.MapPost("/auth/breakglass", async (BreakglassRequest request, HttpContext context, SignInConfig config, Services services) =>
        {
            var password = request.Password ?? "";
            if (password.Length is 0 or > 256) return Refused(StatusCodes.Status400BadRequest, "missing", "Type the breakglass password.");
            var user = await services.Users.FindAsync(AppUser.BreakglassId);
            if (user?.PasswordHash is null || !config.Admits(user)) return Results.NotFound();
            if (services.Throttle.Locked(AppUser.BreakglassId) is { } locked) return Locked(locked);
            if (!PasswordHash.Verify(user.PasswordHash, password))
            {
                services.Throttle.Failed(AppUser.BreakglassId);
                await LogAsync(context, services, user.Upn, null, "", "sign-in failed: wrong password");
                services.Log.LogWarning("a breakglass sign-in from {Address} was refused", ClientAddress.Of(context));
                return Refused(StatusCodes.Status401Unauthorized, "sign-in-failed", "Wrong password.");
            }
            services.Throttle.Succeeded(AppUser.BreakglassId);
            await LogAsync(context, services, user.Upn, user.Id, "", "signed in");
            services.Log.LogWarning("the breakglass administrator signed in from {Address}", ClientAddress.Of(context));
            return await SignedInAsync(context, await services.Users.SignInKnownAsync(user.Id), request.ReturnUrl);
        }).RequireRateLimiting(RateLimiting.Password);
    }

    /// <summary>What the endpoints need, in one parameter.</summary>
    public sealed class Services(UserStore users, SignInThrottle throttle, ConnectionLogger log, TargetCatalog catalog,
        HostEndpoints endpoints, HostConnector connector, CredentialVault vault, IbmLogins logins, TimeProvider clock,
        SettingsStore settings, ILogger<Services> logger)
    {
        public SettingsStore Settings => settings;
        public UserStore Users => users;
        public SignInThrottle Throttle => throttle;
        public ConnectionLogger ConnectionLog => log;
        public TargetCatalog Catalog => catalog;
        public HostEndpoints Endpoints => endpoints;
        public HostConnector Connector => connector;
        public CredentialVault Vault => vault;
        public IbmLogins Logins => logins;
        public TimeProvider Clock => clock;
        public ILogger Log => logger;
    }

    public static string Name(SignInMode mode) => mode switch
    {
        SignInMode.Setup => "setup",
        SignInMode.Entra => "entra",
        SignInMode.Local => "local",
        SignInMode.IbmI => "ibmi",
        SignInMode.Dev => "dev",
        _ => "guest",
    };

    private static async Task<IResult> LocalAsync(HttpContext context, Services services, string name, string password,
        string? newPassword, string? returnUrl)
    {
        if (services.Throttle.Locked(name) is { } locked) return Locked(locked);
        var user = await services.Users.FindByUpnAsync(name);
        var account = user is { Source: UserSource.Local, Disabled: false } ? user : null;
        // The same answer, after the same work, whether the name or the password is wrong.
        if (!PasswordHash.Verify(account?.PasswordHash, password) || account is null)
        {
            services.Throttle.Failed(name);
            await LogAsync(context, services, name, null, "", "sign-in failed: wrong user name or password");
            return Refused(StatusCodes.Status401Unauthorized, "sign-in-failed", "Wrong user name or password.");
        }
        services.Throttle.Succeeded(name);
        if (newPassword is null)
        {
            if (account.MustChangePassword)
                return Refused(StatusCodes.Status409Conflict, "password-change-required", "Choose a new password to sign in.");
            await LogAsync(context, services, account.Upn, account.Id, "", "signed in");
            return await SignedInAsync(context, await services.Users.SignInKnownAsync(account.Id), returnUrl);
        }

        if (PasswordHash.Problem(newPassword) is { } problem)
            return Refused(StatusCodes.Status422UnprocessableEntity, "new-password-refused", problem);
        if (newPassword == password)
            return Refused(StatusCodes.Status422UnprocessableEntity, "new-password-refused", "The new password must differ from the current one.");
        var hash = PasswordHash.Create(newPassword);
        await services.Users.UpdateAsync(account.Id, u =>
        {
            u.PasswordHash = hash;
            u.MustChangePassword = false;
        });
        await LogAsync(context, services, account.Upn, account.Id, "", "password changed");
        return await SignedInAsync(context, await services.Users.SignInKnownAsync(account.Id), returnUrl);
    }

    private static async Task<IResult> IbmAsync(HttpContext context, SignInConfig config, Services services, string name,
        string password, string? newPassword, string? returnUrl)
    {
        var target = config.IbmConnection is { } connection ? await services.Catalog.FindAsync(connection) : null;
        if (target is null)
            return Refused(StatusCodes.Status503ServiceUnavailable, "no-system",
                "The IBM i that checks passwords isn't available. An administrator must enable its connection.");
        var profile = name.ToUpperInvariant();
        try
        {
            var endpoint = await services.Endpoints.ForAsync(target, HostService.SignOn, context.RequestAborted);
            try
            {
                if (newPassword is not null)
                {
                    await SignOnServer.ChangePasswordAsync(services.Connector, endpoint, name, password, newPassword, context.RequestAborted);
                    password = newPassword;
                    await LogAsync(context, services, profile, null, target.Name, "password changed", profile);
                }
                var info = await SignOnServer.VerifyAsync(services.Connector, endpoint, name, password, context.RequestAborted);
                profile = info.User;
            }
            catch (SignOnException e) when (e.Failure == SignOnFailure.Unreachable)
            {
                // Perhaps the port moved: ask the port mapper again next time.
                services.Endpoints.Forget(target, HostService.SignOn);
                throw;
            }
        }
        catch (SignOnException e)
        {
            // Nothing reached the IBM i: not worth a log row.
            if (e.Failure is SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword && e.Code == 0)
                return Refused(StatusCodes.Status401Unauthorized, "sign-in-failed", WrongUserOrPassword);
            await LogAsync(context, services, profile.Length <= 10 ? profile : profile[..10], null, target.Name,
                $"sign-in failed: {Words(e.Failure)}", profile.Length <= 10 ? profile : null);
            return e.Failure switch
            {
                SignOnFailure.UnknownUser or SignOnFailure.WrongPassword or SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword =>
                    Refused(StatusCodes.Status401Unauthorized, "sign-in-failed", WrongUserOrPassword),
                SignOnFailure.PasswordExpired =>
                    Refused(StatusCodes.Status409Conflict, "password-expired", "Your IBM i password has expired. Choose a new one to sign in."),
                SignOnFailure.NewPasswordRefused or SignOnFailure.ChangeNotAllowed =>
                    Refused(StatusCodes.Status422UnprocessableEntity, "new-password-refused", e.Message),
                SignOnFailure.Unreachable or SignOnFailure.Protocol => Refused(StatusCodes.Status502BadGateway, "unreachable", e.Message),
                _ => Refused(StatusCodes.Status401Unauthorized, "sign-in-failed", e.Message),
            };
        }

        ClaimsIdentity? identity;
        try
        {
            identity = await services.Users.SignInAsync(UserStore.IbmUserId(profile), profile, "", UserSource.IbmI);
        }
        catch (NameTakenException)
        {
            return Refused(StatusCodes.Status409Conflict, "name-taken",
                "An account from another sign-in method has this name. An administrator must remove it first.");
        }
        if (identity is null)
        {
            await LogAsync(context, services, profile, null, target.Name, "sign-in failed: account switched off", profile);
            return Refused(StatusCodes.Status401Unauthorized, "sign-in-failed", "An administrator has switched this account off.");
        }
        var userId = identity.FindFirst(Claims.ObjectId)!.Value;
        await ApplyIbmAdminsAsync(context, services, target, userId, profile, password);
        // The password just checked signs the terminal and the printer panel on at that system.
        await services.Logins.SignedInAsync(userId, target, profile, password);
        await LogAsync(context, services, profile, userId, target.Name, "signed in", profile);
        return await SignedInAsync(context, identity, returnUrl);
    }

    /// <summary>
    /// When the IBM i decides who is an administrator (IbmAdmins): asks it about this profile, with
    /// the password just checked, and gives or takes the role. Administrators named on the admin
    /// page keep it. A lookup that fails takes the IBM i's grant away (fails closed) but never
    /// stops the sign-in.
    /// </summary>
    private static async Task ApplyIbmAdminsAsync(HttpContext context, Services services, Target target, string userId,
        string profile, string password)
    {
        var rule = IbmAdmins.From(services.Settings);
        if (!rule.IsSet) return;
        bool admit;
        string reason;
        try
        {
            var endpoint = await services.Endpoints.ForAsync(target, HostService.RemoteCommand, context.RequestAborted);
            var found = await RemoteCommand.CurrentUserAsync(services.Connector, endpoint, profile, password, context.RequestAborted);
            admit = rule.Admits(found);
            reason = admit ? "the IBM i makes it an administrator" : "the IBM i doesn't make it an administrator";
        }
        catch (SignOnException e)
        {
            if (e.Failure == SignOnFailure.Unreachable) services.Endpoints.Forget(target, HostService.RemoteCommand);
            services.Log.LogWarning("reading {Profile}'s groups and special authorities on {Connection} failed: {Failure}: {Message}",
                profile, target.Name, e.Failure, e.Message);
            admit = false;
            reason = "the IBM i couldn't say whether it is an administrator";
        }
        var changed = false;
        await services.Users.UpdateAsync(userId, u =>
        {
            if (admit && !u.IsAdmin) (u.IsAdmin, u.AdminFromIbmI, changed) = (true, true, true);
            else if (!admit && u.AdminFromIbmI) (u.IsAdmin, u.AdminFromIbmI, changed) = (false, false, true);
        });
        if (changed)
            await LogAsync(context, services, profile, userId, target.Name,
                admit ? $"administrator: {reason}" : $"no longer an administrator: {reason}", profile);
    }

    private const string WrongUserOrPassword =
        "Wrong user or password. Each wrong password counts toward the IBM i disabling the user profile.";

    private static async Task<IResult> SignedInAsync(HttpContext context, ClaimsIdentity identity, string? returnUrl)
    {
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Json(new { redirect = AuthSetup.SafeReturn(returnUrl) });
    }

    internal static IResult Refused(int status, string error, string message) => Results.Json(new { error, message }, statusCode: status);

    private static IResult Locked(TimeSpan left) => Refused(StatusCodes.Status429TooManyRequests, "locked",
        $"Too many wrong passwords. Try again in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} minutes.");

    /// <summary>A row that starts and ends at once: who tried, from where, and how it went.</summary>
    internal static async Task LogAsync(HttpContext context, Services services, string name, string? userId, string connection,
        string outcome, string? ibmUser = null)
    {
        var now = services.Clock.GetUtcNow();
        await services.ConnectionLog.AddAsync(new ConnectionLog
        {
            Kind = LogKind.SignIn,
            SessionId = Guid.NewGuid(),
            UserId = userId,
            Upn = name,
            ClientIp = ClientAddress.Of(context),
            ConnectionName = connection,
            IbmUser = ibmUser,
            StartedAt = now,
            EndedAt = now,
            EndReason = outcome,
        });
    }

    internal static string Words(SignOnFailure failure) => failure switch
    {
        SignOnFailure.WrongPassword => "wrong password",
        SignOnFailure.WrongPasswordDisabled => "wrong password, profile disabled",
        SignOnFailure.ProfileDisabled => "profile disabled",
        SignOnFailure.PasswordExpired => "password expired",
        SignOnFailure.UnknownUser => "unknown user",
        SignOnFailure.NoPassword => "no password",
        SignOnFailure.NewPasswordRefused => "new password refused",
        SignOnFailure.ChangeNotAllowed => "password change not allowed",
        SignOnFailure.Unreachable => "sign-on server unreachable",
        _ => failure.ToString().ToLowerInvariant(),
    };
}
