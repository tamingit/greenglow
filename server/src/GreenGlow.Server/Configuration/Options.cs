namespace GreenGlow.Server.Configuration;

/// <summary>Where the server is reached and what it serves.</summary>
public sealed class ServerOptions
{
    /// <summary>
    /// The external base URL (https://host behind the proxy). Its origin is allowed to open
    /// sessions, and share links are built on it. (The sign-in redirect follows the address
    /// the browser used: behind the proxy, with TrustForwardedHeaders, that is this URL.)
    /// </summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// More browser origins allowed to open a WebSocket, such as the Vite dev server
    /// (http://localhost:5173). The PublicUrl origin is always allowed.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Behind a reverse proxy that terminates TLS: take the client address and scheme from
    /// X-Forwarded-For and X-Forwarded-Proto. Leave off when browsers reach the server directly.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>The built web app. Unset or missing means no static files.</summary>
    public string? StaticDir { get; set; }
}

/// <summary>Connections to the IBM i systems.</summary>
public sealed class RelayOptions
{
    /// <summary>CA that issued the IBM i certificate. The only CA trusted for tls:// targets.</summary>
    public string CaFile { get; set; } = "/app/certs/ibmi-ca.pem";

    /// <summary>The same CA as PEM text; when set, it is used instead of CaFile.</summary>
    public string? CaPem { get; set; }
}

/// <summary>
/// Per-user request rate limits (RateLimit__*). They are a safety net on top of the real
/// controls — Entra ID throttles and locks web sign-in, and the IBM i disables a profile after
/// repeated bad passwords (QMAXSIGN). These stop the two surfaces the app itself brokers from
/// being driven hard: the directory search can't be used to enumerate Entra ID quickly, and
/// printer sign-on can't be used to guess an IBM i password quickly. The partition is the
/// signed-in user (their object ID), or the client address when there is none.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Turn the limiter off. On by default; the test host sets it off except where tested.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory searches one user may run per minute (/api/directory/*).</summary>
    public int DirectoryPerMinute { get; set; } = 60;

    /// <summary>Printer sign-on attempts one user may make per minute (/api/printer/*/signon).</summary>
    public int SignOnPerMinute { get; set; } = 10;
}

/// <summary>
/// The public demo (Demo__*): visitors sign in as guests, without an account, and may use only
/// connections marked as demo systems. Sharing, the directory and administration are off. The
/// server refuses to start in demo mode with Entra ID, the dev login, an initial administrator
/// or any connection that isn't a demo system.
/// </summary>
public sealed class DemoOptions
{
    public bool Enabled { get; set; }

    /// <summary>How long a guest's sign-in lasts (no sliding renewal).</summary>
    public int GuestHours { get; set; } = 8;

    /// <summary>New guests one client address may start per hour.</summary>
    public int GuestsPerHour { get; set; } = 10;
}

/// <summary>Printer output: the IBM i Network Print server.</summary>
public sealed class PrinterOptions
{
    /// <summary>How long a signed-on print session lasts without use.</summary>
    public int IdleMinutes { get; set; } = 15;

    /// <summary>
    /// Another port for the Network Print server, for the fake IBM i in tests. Production always
    /// uses IBM's: 8474, and 9474 over TLS.
    /// </summary>
    public int? Port { get; set; }

    public int? SecurePort { get; set; }
}
