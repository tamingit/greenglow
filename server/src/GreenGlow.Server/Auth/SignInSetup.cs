using System.Security.Claims;
using System.Text.RegularExpressions;
using GreenGlow.Server.Admin;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Logins;
using GreenGlow.Server.People;
using GreenGlow.Server.Relay;
using GreenGlow.Server.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Auth;

public sealed record EntraChoice(string? TenantId, string? ClientId, string? ClientSecret);

/// <summary>The first local administrator (or, when changing method later, another one).</summary>
public sealed record LocalChoice(string? User, string? Name, string? Password);

/// <summary>
/// IBM i sign-in: the system that checks passwords — an existing connection by name, or a new
/// one to add — and an IBM i user and password on it, checked there and then: it proves the
/// system can be used to sign in, and that user becomes an administrator.
/// </summary>
public sealed record IbmChoice(string? Connection, ConnectionEndpoints.ConnectionRequest? NewConnection, string? User, string? Password);

/// <summary>POST /auth/setup, and PUT /api/admin/signin (without the token).</summary>
public sealed record SignInChoice(string? BreakglassPassword, string? Method, EntraChoice? Entra, LocalChoice? Local,
    IbmChoice? Ibmi, string? PublicUrl = null);

/// <summary>
/// Chooses how people sign in: at first-run setup, and later on the admin page. Everything a
/// choice needs is written in one save: the breakglass administrator, the method and its
/// details, the connection and the first administrator. Nothing is saved when any part is
/// refused, such as an IBM i password the IBM i doesn't accept.
/// </summary>
public sealed partial class SignInSetup(
    IDbContextFactory<GreenGlowDb> dbs,
    SignInConfig config,
    SettingsStore settings,
    HostEndpoints endpoints,
    HostConnector connector,
    IbmLogins logins,
    SeedRunner seed,
    TimeProvider clock,
    ILogger<SignInSetup> log)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$")]
    private static partial Regex LocalName();

    public static bool IsLocalName(string name) =>
        LocalName().IsMatch(name) && !string.Equals(name, AppUser.BreakglassId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The outcome: a problem to answer with, or the administrator to sign the caller in as (if any).</summary>
    public sealed record Outcome(IResult? Problem, AppUser? SignInAs, string? Warning = null);

    /// <param name="initial">First-run setup: the server must not be set up yet, and a breakglass password is required.</param>
    public async Task<Outcome> ChooseAsync(SignInChoice choice, bool initial, CancellationToken cancel)
    {
        await _one.WaitAsync(cancel);
        try
        {
            if (config.FromEnvironment)
                return Problem(StatusCodes.Status409Conflict, "from-environment", "The sign-in method is set in the server's environment.");
            if (initial && config.Mode != SignInMode.Setup)
                return Problem(StatusCodes.Status409Conflict, "already-set-up", "This server is already set up. Sign in to change it.");

            // The address people open GreenGlow at. Setup needs it, so the origin check can allow it.
            var publicUrl = PublicAddress.Parse(choice.PublicUrl);
            if (initial && publicUrl is null)
                return Field("publicUrl", "Enter the address people open GreenGlow at, such as http://localhost:8080 or https://greenglow.example.com.");
            if (choice.PublicUrl is not null && publicUrl is null)
                return Field("publicUrl", "The address is an origin such as http://localhost:8080 or https://greenglow.example.com, with no path.");

            await using var db = await dbs.CreateDbContextAsync(cancel);
            var now = clock.GetUtcNow();
            var breakglass = await db.Users.FindAsync([AppUser.BreakglassId], cancel);
            if (choice.BreakglassPassword is { Length: > 0 } breakglassPassword)
            {
                if (PasswordHash.Problem(breakglassPassword) is { } weak) return Field("breakglassPassword", weak);
                if (breakglass is null)
                {
                    breakglass = new AppUser
                    {
                        Id = AppUser.BreakglassId,
                        Upn = AppUser.BreakglassId,
                        DisplayName = "Breakglass administrator",
                        IsAdmin = true,
                        Source = UserSource.Breakglass,
                        FirstSeenAt = now,
                    };
                    db.Users.Add(breakglass);
                }
                breakglass.PasswordHash = PasswordHash.Create(breakglassPassword);
            }
            else if (breakglass?.PasswordHash is null)
                return Field("breakglassPassword", "Choose a breakglass password: it is the way in if the sign-in method stops working.");

            var values = new Dictionary<string, string?>
            {
                [AuthKeys.EntraTenantId] = null,
                [AuthKeys.EntraClientId] = null,
                [AuthKeys.EntraClientSecret] = null,
                [AuthKeys.IbmConnection] = null,
            };
            AppUser? admin = null;
            (Target Target, string User, string Password)? ibm = null;
            switch (choice.Method?.Trim().ToLowerInvariant())
            {
                case "entra":
                {
                    var tenant = choice.Entra?.TenantId?.Trim() ?? "";
                    var client = choice.Entra?.ClientId?.Trim() ?? "";
                    var secret = choice.Entra?.ClientSecret ?? "";
                    if (!Guid.TryParse(tenant, out _)) return Field("entra.tenantId", "The directory (tenant) ID is a UUID, from the app registration's overview.");
                    if (!Guid.TryParse(client, out _)) return Field("entra.clientId", "The application (client) ID is a UUID, from the app registration's overview.");
                    // Changing other details keeps the secret already stored.
                    var stored = settings.Text(AuthKeys.EntraClientSecret);
                    if (secret.Length == 0 && (initial || string.IsNullOrEmpty(stored)))
                        return Field("entra.clientSecret", "Enter the client secret's value.");
                    values[AuthKeys.Method] = "entra";
                    values[AuthKeys.EntraTenantId] = tenant;
                    values[AuthKeys.EntraClientId] = client;
                    values[AuthKeys.EntraClientSecret] = secret.Length > 0 ? config.Protect(secret) : stored;
                    break;
                }
                case "local":
                {
                    var name = choice.Local?.User?.Trim() ?? "";
                    var existing = name.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.Upn == name, cancel);
                    if (name.Length == 0)
                    {
                        // Changing method later: fine as long as some local administrator can sign in.
                        if (initial || !await db.Users.AnyAsync(u => u.Source == UserSource.Local && u.IsAdmin && !u.Disabled && u.PasswordHash != null, cancel))
                            return Field("local.user", "Enter a user name for the first administrator.");
                    }
                    else
                    {
                        if (!IsLocalName(name))
                            return Field("local.user", "A user name is up to 64 letters, digits, dots, dashes, underscores or @.");
                        if (existing is not null && existing.Source != UserSource.Local)
                            return Field("local.user", "An account from another sign-in method has that name. Remove it first, or choose another name.");
                        if (PasswordHash.Problem(choice.Local?.Password) is { } weak) return Field("local.password", weak);
                        admin = existing ?? new AppUser { Id = Guid.CreateVersion7().ToString(), Upn = name, Source = UserSource.Local, FirstSeenAt = now };
                        if (existing is null) db.Users.Add(admin);
                        admin.DisplayName = choice.Local?.Name?.Trim() is { Length: > 0 and <= 128 } display ? display : name;
                        admin.PasswordHash = PasswordHash.Create(choice.Local!.Password!);
                        admin.MustChangePassword = false;
                        admin.Disabled = false;
                        admin.IsAdmin = true;
                    }
                    values[AuthKeys.Method] = "local";
                    break;
                }
                case "ibmi":
                {
                    HostConnection? connection;
                    if (choice.Ibmi?.NewConnection is { } request)
                    {
                        var name = request.Name?.Trim() ?? "";
                        if (!TargetParser.IsValidName(name))
                            return Field("ibmi.newConnection.name", "The name must start with a letter and use lowercase letters, digits and dashes (up to 32).");
                        if (await db.Connections.AnyAsync(c => c.Name == name, cancel))
                            return Field("ibmi.newConnection.name", $"There is already a connection called {name}.");
                        connection = new HostConnection { Name = name, Label = name, Host = "", CreatedAt = now, UpdatedAt = now };
                        if (ConnectionEndpoints.Apply(connection, request with { Url = request.Url ?? "" }) is { } problem)
                            return new Outcome(problem, null);
                        connection.SortOrder = (await db.Connections.Select(c => (int?)c.SortOrder).MaxAsync(cancel) ?? -1) + 1;
                        // Whoever can sign in with that IBM i may open sessions on it.
                        connection.Grants.Add(new ConnectionGrant
                        {
                            PrincipalType = PrincipalType.Group,
                            PrincipalId = DatabaseDirectory.EveryoneId,
                            DisplayName = DatabaseDirectory.EveryoneName,
                        });
                        db.Connections.Add(connection);
                    }
                    else
                    {
                        var name = choice.Ibmi?.Connection?.Trim() ?? "";
                        connection = await db.Connections.FirstOrDefaultAsync(c => c.Name == name, cancel);
                        if (connection is null) return Field("ibmi.connection", "Choose the IBM i system that checks passwords, or add it.");
                    }
                    if (!connection.Enabled) return Field("ibmi.connection", "That connection is disabled. Enable it first.");
                    var user = choice.Ibmi?.User?.Trim() ?? "";
                    var password = choice.Ibmi?.Password ?? "";
                    var target = TargetCatalog.ToTarget(connection);
                    SignOnInfo info;
                    try
                    {
                        var endpoint = await endpoints.ForAsync(target, HostService.SignOn, cancel);
                        info = await SignOnServer.VerifyAsync(connector, endpoint, user, password, cancel);
                    }
                    catch (SignOnException e)
                    {
                        endpoints.Forget(target, HostService.SignOn);
                        log.LogInformation("choosing IBM i sign-in on {Connection}: the check failed: {Failure}", connection.Name, e.Failure);
                        return Field(e.Failure is SignOnFailure.Unreachable or SignOnFailure.Protocol ? "ibmi.connection" : "ibmi.user",
                            e.Failure is SignOnFailure.Unreachable or SignOnFailure.Protocol
                                ? $"{e.Message} IBM i sign-in needs that system's sign-on server."
                                : e.Message);
                    }
                    var id = UserStore.IbmUserId(info.User);
                    admin = await db.Users.FindAsync([id], cancel);
                    if (admin is null)
                    {
                        if (await db.Users.AnyAsync(u => u.Upn == info.User, cancel))
                            return Field("ibmi.user", "An account from another sign-in method has that name. Remove it first.");
                        admin = new AppUser { Id = id, Upn = info.User, DisplayName = info.User, Source = UserSource.IbmI, FirstSeenAt = now };
                        db.Users.Add(admin);
                    }
                    admin.IsAdmin = true;
                    admin.Disabled = false;
                    values[AuthKeys.Method] = "ibmi";
                    values[AuthKeys.IbmConnection] = connection.Name;
                    ibm = (target, info.User, password);
                    break;
                }
                default:
                    return Field("method", "Choose Entra ID, local accounts or IBM i sign-in.");
            }

            if (publicUrl is not null) values[PublicAddress.Setting] = publicUrl;
            SettingsStore.Apply(db, values);
            await db.SaveChangesAsync(cancel);
            settings.Forget();
            await config.ApplyAsync();
            log.LogWarning("the sign-in method is now {Method}", values[AuthKeys.Method]);

            string? warning = null;
            if (initial)
            {
                // The seeded connections waited for a directory to look their access lists up in.
                try
                {
                    await seed.ApplyAsync();
                }
                catch (Exception e) when (e is ConfigException or HttpRequestException or TaskCanceledException)
                {
                    warning = $"The seeded connections were not added: {e.Message}";
                    log.LogWarning("seeding connections after setup failed: {Error}", e.Message);
                }
            }
            if (ibm is { } signedOn && admin is not null)
                await logins.SignedInAsync(admin.Id, signedOn.Target, signedOn.User, signedOn.Password);
            return new Outcome(null, admin ?? breakglass, warning);
        }
        finally
        {
            _one.Release();
        }
    }

    private static Outcome Problem(int status, string error, string message) =>
        new(PasswordLogin.Refused(status, error, message), null);

    private static Outcome Field(string field, string message) => new(ConnectionEndpoints.Problem(field, message), null);
}

/// <summary>The connections to seed (Seed__*), applied at start, or after first-run setup when there was nobody to ask before.</summary>
public sealed class SeedRunner(IReadOnlyList<HostConnection> seed, IDbContextFactory<GreenGlowDb> dbs, IDirectory directory, TimeProvider clock)
{
    public async Task<IReadOnlyList<string>> ApplyAsync()
    {
        if (seed.Count == 0) return [];
        await using var db = await dbs.CreateDbContextAsync();
        // A fresh copy each time: applying changes the rows it is given.
        return await ConnectionSeed.ApplyAsync(db, seed.Select(Copy).ToList(), directory, clock);
    }

    private static HostConnection Copy(HostConnection c) => new()
    {
        Name = c.Name,
        Label = c.Label,
        Host = c.Host,
        Port = c.Port,
        Secure = c.Secure,
        MaxSessionsPerUser = c.MaxSessionsPerUser,
        IsTest = c.IsTest,
        IsDemo = c.IsDemo,
        Enabled = c.Enabled,
        Color = c.Color,
        UsePortMapper = c.UsePortMapper,
        Grants = c.Grants.Select(g => new ConnectionGrant
        {
            PrincipalType = g.PrincipalType,
            PrincipalId = g.PrincipalId,
            DisplayName = g.DisplayName,
        }).ToList(),
    };
}

/// <summary>
/// First-run setup: GET /auth/setup says whether it is needed; POST /auth/setup does it. After
/// it, the server answers 409: later changes are an administrator's, on the admin page.
/// </summary>
public static class SetupEndpoints
{
    /// <summary>
    /// The setup page exists only while setup is needed: once the server is set up, /setup.html
    /// answers 404, whatever the static files hold.
    /// </summary>
    public static void UseSetupPage(this WebApplication app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.Equals(AuthSetup.SetupPage, StringComparison.OrdinalIgnoreCase)
                && context.RequestServices.GetRequiredService<SignInConfig>().Mode != SignInMode.Setup)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            return next(context);
        });

    public static void MapSetup(this WebApplication app)
    {
        app.MapGet("/auth/setup", (SignInConfig config) => Results.Json(new { needed = config.Mode == SignInMode.Setup }));

        app.MapPost("/auth/setup", async (SignInChoice choice, HttpContext context, SignInConfig config, SignInSetup setup,
            UserStore users, PasswordLogin.Services services) =>
        {
            if (config.Mode != SignInMode.Setup)
                return PasswordLogin.Refused(StatusCodes.Status409Conflict, "already-set-up", "This server is already set up. Sign in to change it.");
            var outcome = await setup.ChooseAsync(choice, initial: true, context.RequestAborted);
            if (outcome.Problem is not null) return outcome.Problem;
            var admin = outcome.SignInAs!;
            await PasswordLogin.LogAsync(context, services, admin.Upn, admin.Id, "", $"first-run setup: {choice.Method?.Trim().ToLowerInvariant()}");
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(await users.SignInKnownAsync(admin.Id)));
            return Results.Json(new { redirect = "/admin.html", warning = outcome.Warning });
        }).RequireRateLimiting(RateLimiting.Password);
    }

    /// <summary>At start: says in the log how to set the server up, with the token.</summary>
    public static void AnnounceSetup(this WebApplication app, ServerOptions server)
    {
        app.Logger.LogWarning(
            "GreenGlow is not set up: nobody can sign in yet. Open {Url}{Page} to choose how people sign in",
            server.PublicUrl.TrimEnd('/'), AuthSetup.SetupPage);
    }
}
