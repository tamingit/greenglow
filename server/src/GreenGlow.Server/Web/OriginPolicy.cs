using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.Web;

/// <summary>
/// Browser origins allowed to open a WebSocket: the public URL's origin plus any configured
/// extras. Cookies ride along on WebSocket upgrades, so this is what stops another site from
/// opening a session in a signed-in user's name.
/// </summary>
public sealed class OriginPolicy
{
    private readonly HashSet<string> _allowed;

    public OriginPolicy(ServerOptions options)
    {
        _allowed = new HashSet<string>(StringComparer.Ordinal) { OriginOf(options.PublicUrl, "Server:PublicUrl") };
        foreach (var origin in options.AllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o)))
            _allowed.Add(OriginOf(origin.Trim(), "Server:AllowedOrigins"));
    }

    public IReadOnlyCollection<string> Allowed => _allowed;

    public bool IsAllowed(string? origin) => !string.IsNullOrEmpty(origin) && _allowed.Contains(origin);

    private static string OriginOf(string value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.AbsolutePath != "/" || uri.Query != "" || uri.UserInfo != "")
            throw new ConfigException($"{setting} entry \"{value}\" must be an origin such as http://localhost:5080");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public static class OriginCheck
{
    /// <summary>
    /// Requests that change something must come from an allowed origin. With cookie sign-in
    /// this stops another site posting in a signed-in user's name (SameSite=Lax covers most of
    /// it; this covers the rest, such as same-site subdomains).
    /// </summary>
    public static void UseOriginCheck(this WebApplication app) =>
        app.Use((context, next) =>
        {
            var method = context.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
                return next(context);
            // Entra ID posts the sign-in result from its own origin; the handler checks state and nonce.
            if (context.Request.Path.Equals(Auth.AuthSetup.CallbackPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(Auth.AuthSetup.SignedOutPath, StringComparison.OrdinalIgnoreCase))
                return next(context);
            var origins = context.RequestServices.GetRequiredService<OriginPolicy>();
            if (origins.IsAllowed(context.Request.Headers.Origin.ToString())) return next(context);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
}
