using System.Security.Claims;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GreenGlow.Server.Auth;

public sealed class EntraOptions
{
    /// <summary>Directory (tenant) ID.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The app registration's client ID.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret for the sign-in code exchange and Microsoft Graph.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// The UPN made an administrator when it signs in. Further administrators are added in the
    /// admin UI.
    /// </summary>
    public string InitialAdminUpn { get; set; } = "";

    public bool Configured => TenantId != "" && ClientId != "";
}

public sealed class AuthOptions
{
    /// <summary>
    /// Development and automated tests only: sign in as any UPN without Entra ID. Honoured only
    /// when the environment is Development or Testing and Entra ID is not configured.
    /// </summary>
    public bool DevLogin { get; set; }

    /// <summary>How long a sign-in lasts without activity.</summary>
    public int SessionHours { get; set; } = 12;
}

/// <summary>Claim types the server adds to the cookie.</summary>
public static class Claims
{
    public const string ObjectId = "oid";
    public const string Upn = "upn";
    public const string Name = "name";
}

/// <summary>How people sign in: Entra ID, the dev login (Development and tests), or as demo guests.</summary>
public enum SignInMode
{
    Entra,
    Dev,
    Guest,
}

public static class AuthSetup
{
    public const string CallbackPath = "/auth/callback";
    public const string AdminPolicy = "admin";
    public const string SignedOutPath = "/auth/signed-out";

    /// <summary>
    /// Cookie sign-in backed by Entra ID (OpenID Connect), by the dev login when allowed, or by
    /// guest sign-in in demo mode. Demo mode is its own guard: it never relaxes the other two.
    /// </summary>
    public static SignInMode AddGreenGlowAuth(this WebApplicationBuilder builder, EntraOptions entra, AuthOptions auth, DemoOptions demo)
    {
        var environment = builder.Environment;
        var devLogin = auth.DevLogin && !entra.Configured
            && (environment.IsDevelopment() || environment.IsEnvironment("Testing"));
        if (demo.Enabled)
        {
            // One way in at a time, and no accounts that could reach anything but demo systems.
            if (entra.Configured)
                throw new ConfigException("Demo mode can't be used with Entra ID: unset Entra__TenantId and Entra__ClientId, or Demo__Enabled.");
            if (auth.DevLogin)
                throw new ConfigException("Demo mode can't be used with the dev login: unset Auth__DevLogin, or Demo__Enabled.");
            if (entra.InitialAdminUpn.Trim().Length > 0)
                throw new ConfigException("Demo mode has no administrators: unset Entra__InitialAdminUpn, or Demo__Enabled.");
            if (demo.GuestHours is < 1 or > 72) throw new ConfigException("Demo__GuestHours must be from 1 to 72.");
            if (demo.GuestsPerHour is < 1 or > 1000) throw new ConfigException("Demo__GuestsPerHour must be from 1 to 1000.");
        }
        else if (!entra.Configured && !devLogin)
            throw new ConfigException(
                "Entra ID is not configured: set Entra__TenantId, Entra__ClientId and Entra__ClientSecret " +
                "(or, in Development only, Auth__DevLogin=true).");
        var mode = demo.Enabled ? SignInMode.Guest : devLogin ? SignInMode.Dev : SignInMode.Entra;

        // Unauthenticated API calls get 401 from the cookie scheme; only /auth/signin goes to Entra ID.
        var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme);
        authentication.AddCookie(o =>
        {
            o.Cookie.Name = "greenglow.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            // A guest's sign-in ends a fixed time after it started; anyone else's after inactivity.
            o.ExpireTimeSpan = TimeSpan.FromHours(mode == SignInMode.Guest ? demo.GuestHours : auth.SessionHours);
            o.SlidingExpiration = mode != SignInMode.Guest;
            o.LoginPath = "/auth/signin";
            // The API and the WebSocket answer 401/403; only pages are sent to sign in.
            o.Events.OnRedirectToLogin = context => Deny(context, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = context => Deny(context, StatusCodes.Status403Forbidden);
            o.Events.OnValidatePrincipal = async context =>
            {
                // A user removed since signing in loses access at once, and the administrator role
                // comes from the database on every request, so granting or removing it applies at once.
                var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
                var id = context.Principal?.FindFirstValue(Claims.ObjectId);
                var user = id is null ? null : await users.FindAsync(id);
                if (user is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }
                if (user.IsAdmin && context.Principal!.Identity is ClaimsIdentity identity)
                    identity.AddClaim(new Claim(identity.RoleClaimType, UserStore.AdminRole));
            };
        });

        if (entra.Configured)
        {
            authentication.AddOpenIdConnect(o =>
            {
                o.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
                o.ClientId = entra.ClientId;
                o.ClientSecret = entra.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.CallbackPath = CallbackPath;
                o.SignedOutCallbackPath = SignedOutPath;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                o.TokenValidationParameters.NameClaimType = "name";
                o.Events.OnTokenValidated = async context =>
                {
                    var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
                    var principal = context.Principal!;
                    var identity = await users.SignInAsync(
                        principal.FindFirstValue("oid") ?? throw new InvalidOperationException("no oid claim"),
                        principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("upn")
                            ?? throw new InvalidOperationException("no UPN claim"),
                        principal.FindFirstValue("name") ?? "");
                    context.Principal = new ClaimsPrincipal(identity);
                };
            });
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(UserStore.AdminRole));
        builder.Services.AddSingleton(new AdminSeed(entra.InitialAdminUpn));
        builder.Services.AddSingleton<UserStore>();
        return mode;
    }

    public static void MapAuthEndpoints(this WebApplication app, SignInMode mode)
    {
        var devLogin = mode == SignInMode.Dev;
        // Signing in: back to the page that asked, which must be on this site.
        app.MapGet("/auth/signin", (string? returnUrl) =>
        {
            var target = SafeReturn(returnUrl);
            return mode switch
            {
                SignInMode.Dev => Results.Redirect($"/auth/dev?returnUrl={Uri.EscapeDataString(target)}"),
                SignInMode.Guest => Results.Redirect($"{GuestLogin.Path}?returnUrl={Uri.EscapeDataString(target)}"),
                _ => Results.Challenge(new AuthenticationProperties { RedirectUri = target },
                    [OpenIdConnectDefaults.AuthenticationScheme]),
            };
        });

        app.MapPost("/auth/signout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return mode switch
            {
                SignInMode.Dev => Results.Redirect("/"),
                SignInMode.Guest => Results.Redirect(GuestLogin.Path),
                _ => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                    [OpenIdConnectDefaults.AuthenticationScheme]),
            };
        }).RequireAuthorization();

        if (mode == SignInMode.Guest) app.MapGuestLogin();

        if (devLogin)
        {
            app.Logger.LogWarning("Auth:DevLogin is on: anyone can sign in as any UPN. Never use this in production.");
            app.MapGet("/auth/dev", (string? returnUrl) => Results.Content(DevLoginPage(SafeReturn(returnUrl)), "text/html"));
            app.MapPost("/auth/dev", async (HttpContext context, UserStore users) =>
            {
                var form = await context.Request.ReadFormAsync();
                var upn = form["upn"].ToString().Trim();
                if (!DevUpn.IsMatch(upn)) return Results.BadRequest("Enter a UPN such as someone@example.com");
                var identity = await users.SignInAsync(DevObjectId(upn), upn, form["name"].ToString().Trim());
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Redirect(SafeReturn(form["returnUrl"]));
            });
        }
    }

    private static readonly System.Text.RegularExpressions.Regex DevUpn =
        new(@"^[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,190}$");

    /// <summary>A stable fake object ID for a dev-login UPN.</summary>
    public static string DevObjectId(string upn) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(upn.ToLowerInvariant()))).ToString();

    /// <summary>A local path only: never another site, so sign-in can't be used to redirect away.</summary>
    internal static string SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";

    private static Task Deny(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static string DevLoginPage(string returnUrl) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>GreenGlow Connect dev sign-in</title></head>
        <body>
          <h1>Dev sign-in</h1>
          <p>Development only: Entra ID is not configured.</p>
          <form method="post" action="/auth/dev">
            <label>UPN <input name="upn" type="email" required autofocus></label>
            <label>Name <input name="name"></label>
            <input type="hidden" name="returnUrl" value="{{System.Net.WebUtility.HtmlEncode(returnUrl)}}">
            <button type="submit">Sign in</button>
          </form>
        </body></html>
        """;
}
