using System.Text.RegularExpressions;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;

namespace GreenGlow.Server.Web;

/// <summary>
/// The address people open GreenGlow at: http or https, the host and port, and a path when the
/// app is under one (a base path, such as https://example.com/greenglow behind a proxy that
/// serves other things at its root). Server__PublicUrl gives it; setup or an administrator may
/// save another, which wins.
/// </summary>
public static partial class PublicAddress
{
    /// <summary>The setting that holds the address people open GreenGlow at, as saved by setup or on the admin page.</summary>
    public const string Setting = "Server.PublicUrl";

    /// <summary>The app's own top-level paths, which a base path can't start with.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "ws", "auth", "branding", "icons", "brand", "help", "assets", "cluster", "healthz", "src",
    };

    /// <summary>One segment of a base path: letters, digits and - . _ ~, and not . or ..</summary>
    [GeneratedRegex(@"^(?!\.{1,2}$)[A-Za-z0-9._~-]{1,64}$")]
    private static partial Regex Segment();

    /// <summary>
    /// An address as it is kept (http or https, host, port, and the base path if any, with no
    /// trailing slash), or null when it is not one: no query, fragment or user, and a path made
    /// of plain segments.
    /// </summary>
    public static string? Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
            return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Uri has already resolved any . and .. away; what's left must be plain.
        if (segments.Length > 8 || !segments.All(s => Segment().IsMatch(s)) || uri.AbsolutePath.Contains("//"))
            return null;
        // The root is served too: a base path named like one of the app's own paths would hide it.
        if (segments.Length > 0 && (Reserved.Contains(segments[0]) || segments[0].Contains('.'))) return null;
        var path = segments.Length == 0 ? "" : "/" + string.Join('/', segments);
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    /// <summary>The origin of an address (scheme, host and port), as a browser sends it in Origin.</summary>
    public static string Origin(string address) => new Uri(address).GetLeftPart(UriPartial.Authority);

    /// <summary>The base path of an address: "" at the root, else such as "/greenglow".</summary>
    public static string BasePath(string address) => new Uri(address).AbsolutePath.TrimEnd('/');

    /// <summary>The address in use: the one saved by setup or an administrator, else the configured Server__PublicUrl.</summary>
    public static string Current(SettingsStore settings, ServerOptions server) =>
        Parse(settings.Text(Setting)) ?? Parse(server.PublicUrl) ?? server.PublicUrl;

    /// <summary>
    /// Serves the app under the address's base path: a request for /greenglow/api/me is handled
    /// as /api/me, with /greenglow as its PathBase, so redirects, sign-in's callback and the pages'
    /// relative addresses keep it. /greenglow alone is sent to /greenglow/, where the pages are.
    /// Requests at the root are still served (the health check, other nodes, a browser reaching the
    /// container directly), so a wrong path never locks anyone out. A proxy that strips the path
    /// says so in X-Forwarded-Prefix (with Server__TrustForwardedHeaders), which sets PathBase
    /// before this runs.
    /// </summary>
    public static void UseBasePath(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var server = app.Services.GetRequiredService<ServerOptions>();
        app.Use((context, next) =>
        {
            var request = context.Request;
            var basePath = BasePath(Current(settings, server));
            if (basePath.Length == 0 || request.PathBase.HasValue
                || !request.Path.StartsWithSegments(basePath, StringComparison.OrdinalIgnoreCase, out var matched, out var rest))
                return next(context);
            if (!rest.HasValue && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            {
                context.Response.Redirect(matched + "/" + request.QueryString);
                return Task.CompletedTask;
            }
            request.PathBase = matched;
            request.Path = rest.HasValue ? rest : "/";
            return next(context);
        });
    }

    /// <summary>
    /// With an https:// public URL, every request is treated as arriving at that scheme and host.
    /// Behind a TLS proxy the server only sees plain http (and maybe the proxy's own host name), so
    /// without this the Entra ID redirect would go to http://, and sign-in's cookies would not be
    /// marked Secure. The proxy is the only way in, so its public address is the true one. With an
    /// http:// public URL (local development) requests are left as they are. The URL is the one in
    /// force at each request: an administrator may change it on the admin page.
    /// </summary>
    public static void UsePublicUrl(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var server = app.Services.GetRequiredService<ServerOptions>();
        app.Use((context, next) =>
        {
            var url = new Uri(Current(settings, server));
            if (url.Scheme == Uri.UriSchemeHttps)
            {
                context.Request.Scheme = Uri.UriSchemeHttps;
                context.Request.Host = new HostString(url.Authority);
            }
            return next(context);
        });
    }

    /// <summary>A path within the app as the browser must ask for it: with the request's base path in front.</summary>
    /// <param name="path">From the app's root, such as /signin.html?returnUrl=...</param>
    public static string Here(HttpContext context, string path) => context.Request.PathBase.ToUriComponent() + path;
}
