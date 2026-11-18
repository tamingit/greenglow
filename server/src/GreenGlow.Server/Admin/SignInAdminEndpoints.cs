using System.Security.Claims;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using GreenGlow.Server.Logins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/signin and /api/admin/users: how people sign in (the method, the breakglass
/// password, whether IBM i passwords may be remembered), and the accounts: every user that
/// has signed in, and, with local accounts, adding them and setting their passwords.
/// Administrators only. No answer ever carries a password, a hash or a secret.
/// </summary>
public static class SignInAdminEndpoints
{
    public sealed record BreakglassRequest(string? Password);

    public sealed record RememberRequest(bool? Allow);

    public sealed record NewUserRequest(string? User, string? Name, string? Password, bool? IsAdmin);

    public sealed record UserRequest(string? Name, string? Password, bool? Disabled, bool? IsAdmin);

    public static void MapSignInAdminEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/signin", async (SignInConfig config, SettingsStore settings, UserStore users, CredentialSeal seal) =>
            Results.Json(await DescribeAsync(config, settings, users, seal)));

        // Changes the method or its details. The caller stays signed in when their account is
        // still good under the new method; otherwise they become the administrator the change
        // named (a local account, an IBM i user), or are signed out (to sign in again).
        admin.MapPut("/signin", async (SignInChoice choice, HttpContext context, SignInConfig config, SignInSetup setup,
            SettingsStore settings, UserStore users, CredentialSeal seal, PasswordLogin.Services services) =>
        {
            var before = config.Mode;
            var outcome = await setup.ChooseAsync(choice, initial: false, context.RequestAborted);
            if (outcome.Problem is not null) return outcome.Problem;
            var me = CurrentUser.From(context.User)!;
            await PasswordLogin.LogAsync(context, services, me.Upn, me.Id, "",
                $"sign-in method: {PasswordLogin.Name(before)} to {PasswordLogin.Name(config.Mode)}");
            var signedOut = false;
            if (await users.FindAsync(me.Id) is not { } account || !config.Admits(account))
            {
                if (outcome.SignInAs is { Source: not UserSource.Breakglass } successor)
                    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(await users.SignInKnownAsync(successor.Id)));
                else
                {
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    signedOut = true;
                }
            }
            return Results.Json(new { signedOut, signin = await DescribeAsync(config, settings, users, seal) });
        });

        admin.MapPut("/signin/breakglass", async (BreakglassRequest request, HttpContext context, UserStore users, TimeProvider clock,
            Microsoft.EntityFrameworkCore.IDbContextFactory<GreenGlowDb> dbs, PasswordLogin.Services services) =>
        {
            if (PasswordHash.Problem(request.Password) is { } weak) return ConnectionEndpoints.Problem("password", weak);
            await using (var db = await dbs.CreateDbContextAsync())
            {
                var breakglass = await db.Users.FindAsync(AppUser.BreakglassId);
                if (breakglass is null)
                {
                    breakglass = new AppUser
                    {
                        Id = AppUser.BreakglassId,
                        Upn = AppUser.BreakglassId,
                        DisplayName = "Breakglass administrator",
                        IsAdmin = true,
                        Source = UserSource.Breakglass,
                        FirstSeenAt = clock.GetUtcNow(),
                    };
                    db.Users.Add(breakglass);
                }
                breakglass.PasswordHash = PasswordHash.Create(request.Password!);
                await db.SaveChangesAsync();
            }
            var me = CurrentUser.From(context.User)!;
            await PasswordLogin.LogAsync(context, services, me.Upn, me.Id, "", "breakglass password changed");
            return Results.NoContent();
        });

        admin.MapPut("/signin/remember", async (RememberRequest request, SettingsStore settings) =>
        {
            await settings.SetTextAsync(new Dictionary<string, string?>
            {
                [IbmLogins.AllowRememberKey] = request.Allow == false ? "false" : null,
            });
            return Results.NoContent();
        });

        admin.MapGet("/users", async (UserStore users, SignInConfig config) => Results.Json(new
        {
            method = PasswordLogin.Name(config.Mode),
            users = (await users.AllAsync()).Select(u => Describe(u, config)),
        }));

        // Local accounts only: other kinds of account appear when they first sign in.
        admin.MapPost("/users", async (NewUserRequest request, UserStore users, SignInConfig config, TimeProvider clock,
            Microsoft.EntityFrameworkCore.IDbContextFactory<GreenGlowDb> dbs) =>
        {
            if (config.Mode != SignInMode.Local)
                return Results.Conflict(new { error = "not-local", message = "Accounts are added here only with local accounts as the sign-in method." });
            var name = request.User?.Trim() ?? "";
            if (!SignInSetup.IsLocalName(name))
                return ConnectionEndpoints.Problem("user", "A user name is up to 64 letters, digits, dots, dashes, underscores or @.");
            if (PasswordHash.Problem(request.Password) is { } weak) return ConnectionEndpoints.Problem("password", weak);
            if (await users.FindByUpnAsync(name) is not null)
                return Results.Conflict(new { error = "name-taken", message = $"There is already an account called {name}." });
            var user = new AppUser
            {
                Id = Guid.CreateVersion7().ToString(),
                Upn = name,
                DisplayName = request.Name?.Trim() is { Length: > 0 and <= 128 } display ? display : name,
                Source = UserSource.Local,
                PasswordHash = PasswordHash.Create(request.Password!),
                // The administrator knows this password: its owner chooses their own at first sign-in.
                MustChangePassword = true,
                IsAdmin = request.IsAdmin == true,
                FirstSeenAt = clock.GetUtcNow(),
            };
            await using var db = await dbs.CreateDbContextAsync();
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Json(Describe(user, config), statusCode: StatusCodes.Status201Created);
        });

        admin.MapPut("/users/{id}", async (string id, UserRequest request, HttpContext context, UserStore users, SignInConfig config) =>
        {
            var user = await users.FindAsync(id);
            if (user is null || user.IsGuest) return Results.NotFound();
            if (user.Source == UserSource.Breakglass)
                return Results.Conflict(new { error = "breakglass", message = "The breakglass administrator only has a password to change." });
            var me = CurrentUser.From(context.User)!;
            if (request.Disabled == true && id == me.Id)
                return Results.Conflict(new { error = "self", message = "You can't switch your own account off." });
            string? hash = null;
            if (request.Password is not null)
            {
                if (user.Source != UserSource.Local)
                    return Results.Conflict(new { error = "not-local", message = "Only a local account's password is set here." });
                if (PasswordHash.Problem(request.Password) is { } weak) return ConnectionEndpoints.Problem("password", weak);
                hash = PasswordHash.Create(request.Password);
            }
            if (request.IsAdmin == false && user.IsAdmin && !await users.RemoveAdminAsync(id))
                return Results.Conflict(new { error = "last-admin", message = "The last administrator can't be removed." });
            if (request.Disabled == true && user.IsAdmin && request.IsAdmin != false
                && (await users.AdminsAsync()).Count(a => !a.Disabled && a.Source != UserSource.Breakglass && a.Id != id) == 0
                && (await users.FindAsync(AppUser.BreakglassId))?.PasswordHash is null)
                return Results.Conflict(new { error = "last-admin", message = "The last administrator can't be switched off." });
            await users.UpdateAsync(id, u =>
            {
                if (request.Name?.Trim() is { Length: > 0 and <= 128 } name) u.DisplayName = name;
                if (request.Disabled is { } disabled) u.Disabled = disabled;
                if (request.IsAdmin == true) u.IsAdmin = true;
                if (hash is not null)
                {
                    u.PasswordHash = hash;
                    u.MustChangePassword = true;
                }
            });
            return Results.Json(Describe((await users.FindAsync(id))!, config));
        });

        // With everything of theirs: colours, saved logins, macros. The connection log keeps its rows.
        admin.MapDelete("/users/{id}", async (string id, HttpContext context, UserStore users, CredentialVault vault) =>
        {
            var user = await users.FindAsync(id);
            if (user is null || user.IsGuest) return Results.NotFound();
            if (user.Source == UserSource.Breakglass)
                return Results.Conflict(new { error = "breakglass", message = "The breakglass administrator can't be removed." });
            if (id == CurrentUser.From(context.User)!.Id)
                return Results.Conflict(new { error = "self", message = "You can't remove your own account." });
            if (user.IsAdmin && !await users.RemoveAdminAsync(id))
                return Results.Conflict(new { error = "last-admin", message = "The last administrator can't be removed." });
            await users.RemoveAsync(id);
            vault.Forget(id);
            return Results.NoContent();
        });
    }

    private static async Task<object> DescribeAsync(SignInConfig config, SettingsStore settings, UserStore users, CredentialSeal seal) => new
    {
        method = PasswordLogin.Name(config.Mode),
        // Fixed by the server's environment (Entra__* variables, the dev login): not changed here.
        fromEnvironment = config.FromEnvironment,
        entra = config.FromEnvironment
            ? null
            : new
            {
                tenantId = settings.Text(AuthKeys.EntraTenantId) ?? "",
                clientId = settings.Text(AuthKeys.EntraClientId) ?? "",
                hasSecret = !string.IsNullOrEmpty(settings.Text(AuthKeys.EntraClientSecret)),
            },
        ibmConnection = config.IbmConnection,
        breakglass = (await users.FindAsync(AppUser.BreakglassId))?.PasswordHash is not null,
        // Remembering IBM i passwords needs Credentials__Key; an administrator may still forbid it.
        rememberAvailable = seal.Available,
        rememberAllowed = settings.Text(IbmLogins.AllowRememberKey) != "false",
    };

    private static object Describe(AppUser u, SignInConfig config) => new
    {
        id = u.Id,
        user = u.Upn,
        name = u.DisplayName,
        source = u.Source switch
        {
            UserSource.Local => "local",
            UserSource.IbmI => "ibmi",
            UserSource.Breakglass => "breakglass",
            _ => "entra",
        },
        isAdmin = u.IsAdmin,
        disabled = u.Disabled,
        mustChangePassword = u.MustChangePassword,
        lastSeenAt = u.LastSeenAt == default ? (DateTimeOffset?)null : u.LastSeenAt,
        // Whether this account can sign in under the method in force.
        canSignIn = !u.Disabled && config.Admits(u) && (u.Source != UserSource.Breakglass || u.PasswordHash is not null),
    };
}
