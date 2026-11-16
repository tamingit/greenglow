using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GreenGlow.Server.Auth;

/// <summary>
/// How people sign in. Entra ID, local accounts and IBM i sign-in are the methods an
/// administrator chooses between (one at a time); Setup is a server nobody has set up yet;
/// Dev (Development and tests) and Guest (demo mode) come from the environment only.
/// </summary>
public enum SignInMode
{
    Setup,
    Entra,
    Local,
    IbmI,
    Dev,
    Guest,
}

/// <summary>The sign-in settings kept in the database (the Settings table), so every node has them.</summary>
public static class AuthKeys
{
    /// <summary>entra, local or ibmi. Missing: not set up.</summary>
    public const string Method = "Auth.Method";
    public const string EntraTenantId = "Auth.Entra.TenantId";
    public const string EntraClientId = "Auth.Entra.ClientId";

    /// <summary>Protected with the server's Data Protection keys.</summary>
    public const string EntraClientSecret = "Auth.Entra.ClientSecret";

    /// <summary>The name of the connection whose IBM i checks passwords, for IBM i sign-in.</summary>
    public const string IbmConnection = "Auth.IbmI.Connection";
}

/// <summary>What the environment fixes about sign-in, decided once at start.</summary>
/// <param name="Mode">Dev or Guest when the environment says so; Entra when Entra__* is set; else null: the database decides.</param>
public sealed record SignInEnvironment(SignInMode? Mode);

/// <summary>
/// The sign-in method in force, read at each request: the environment's when it fixes one
/// (the dev login, demo mode, or Entra__* variables), and otherwise the one an administrator
/// chose at first-run setup or on the admin page. Changing it takes effect at once, on every
/// node: <see cref="ApplyAsync"/> brings Entra ID's settings and its sign-in scheme in line.
/// </summary>
public sealed class SignInConfig(
    SignInEnvironment environment,
    SettingsStore settings,
    EntraOptions entra,
    IDataProtectionProvider protection,
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcCache,
    ILogger<SignInConfig> log)
{
    private readonly IDataProtector _protector = protection.CreateProtector("GreenGlow.Settings.v1");
    private readonly SemaphoreSlim _applying = new(1, 1);

    /// <summary>The environment fixes the method: it can't be changed on the admin page.</summary>
    public bool FromEnvironment => environment.Mode is not null;

    public SignInMode Mode => environment.Mode ?? settings.Text(AuthKeys.Method) switch
    {
        "entra" when StoredEntra() is not null => SignInMode.Entra,
        "local" => SignInMode.Local,
        "ibmi" when IbmConnection is not null => SignInMode.IbmI,
        _ => SignInMode.Setup,
    };

    /// <summary>The connection whose IBM i checks passwords, when the method is IBM i sign-in.</summary>
    public string? IbmConnection => settings.Text(AuthKeys.IbmConnection) is { Length: > 0 } name ? name : null;

    /// <summary>Whether this kind of account may be signed in under the method in force.</summary>
    public bool Admits(AppUser user) => user.Source switch
    {
        // Set up once, good under every method an administrator can choose.
        UserSource.Breakglass => Mode is SignInMode.Entra or SignInMode.Local or SignInMode.IbmI,
        UserSource.Entra => Mode is SignInMode.Entra or SignInMode.Dev or SignInMode.Guest,
        UserSource.Local => Mode == SignInMode.Local,
        UserSource.IbmI => Mode == SignInMode.IbmI,
        _ => false,
    };

    public string Protect(string secret) => _protector.Protect(secret);

    /// <summary>The Entra ID settings in the database, or null when they aren't complete.</summary>
    private (string TenantId, string ClientId, string ClientSecret)? StoredEntra()
    {
        var tenant = settings.Text(AuthKeys.EntraTenantId);
        var client = settings.Text(AuthKeys.EntraClientId);
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(client)) return null;
        var secret = "";
        if (settings.Text(AuthKeys.EntraClientSecret) is { Length: > 0 } sealedSecret)
        {
            try
            {
                secret = _protector.Unprotect(sealedSecret);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                log.LogWarning("the stored Entra ID client secret can't be read with this server's keys; enter it again on the admin page");
            }
        }
        return (tenant, client, secret);
    }

    /// <summary>
    /// Brings the running server in line with the settings: Entra ID's details, and whether its
    /// sign-in scheme exists at all (only while Entra ID is the method, so nothing answers
    /// /auth/callback otherwise). Called at start, after a change, and when a peer's change arrives.
    /// </summary>
    public async Task ApplyAsync()
    {
        await _applying.WaitAsync();
        try
        {
            if (environment.Mode is null)
            {
                var stored = settings.Text(AuthKeys.Method) == "entra" ? StoredEntra() : null;
                (entra.TenantId, entra.ClientId, entra.ClientSecret) = stored ?? ("", "", "");
                oidcCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme);
            }
            var wanted = Mode == SignInMode.Entra;
            var present = await schemes.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme) is not null;
            if (wanted && !present)
                schemes.AddScheme(new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)));
            else if (!wanted && present)
                schemes.RemoveScheme(OpenIdConnectDefaults.AuthenticationScheme);
        }
        finally
        {
            _applying.Release();
        }
    }
}

/// <summary>
/// Slows password guessing against local accounts and the breakglass administrator: after
/// five wrong passwords for a name, that name is locked for five minutes on this node. (An
/// IBM i counts wrong passwords itself, and disables the profile: QMAXSIGN.)
/// </summary>
public sealed class SignInThrottle(TimeProvider clock)
{
    public const int Attempts = 5;
    public static readonly TimeSpan Lock = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTimeOffset Until)> _names = new();

    /// <summary>How much longer the name is locked, or null when it may try.</summary>
    public TimeSpan? Locked(string name)
    {
        if (!_names.TryGetValue(Key(name), out var state)) return null;
        var left = state.Until - clock.GetUtcNow();
        return state.Failures >= Attempts && left > TimeSpan.Zero ? left : null;
    }

    public void Failed(string name)
    {
        var now = clock.GetUtcNow();
        _names.AddOrUpdate(Key(name), _ => (1, now + Lock), (_, old) =>
            old.Until <= now ? (1, now + Lock) : (old.Failures + 1, now + Lock));
        // Names nobody has tried for a while are dropped, so the table can't grow without end.
        if (_names.Count > 10_000)
            foreach (var entry in _names.Where(e => e.Value.Until <= now)) _names.TryRemove(entry);
    }

    public void Succeeded(string name) => _names.TryRemove(Key(name), out _);

    private static string Key(string name) => name.Trim().ToLowerInvariant() is { Length: <= 128 } key ? key : name.Trim()[..128].ToLowerInvariant();
}
