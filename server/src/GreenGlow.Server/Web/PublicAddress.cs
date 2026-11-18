using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;

namespace GreenGlow.Server.Web;

public static class PublicAddress
{
    /// <summary>The setting that holds the address people open GreenGlow at, as saved by setup.</summary>
    public const string Setting = "Server.PublicUrl";

    /// <summary>
    /// The origin of an address (http or https, host and port, nothing else), or null when it is
    /// not one. The stored form has no trailing slash.
    /// </summary>
    public static string? Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != ""
            || uri.UserInfo != "")
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The address in use: the one setup saved, else the configured Server__PublicUrl.</summary>
    public static string Current(GreenGlow.Server.Data.SettingsStore settings, ServerOptions server) =>
        Parse(settings.Text(Setting)) ?? server.PublicUrl;

    /// <summary>
    /// With an https:// public URL, every request is treated as arriving at that scheme and host.
    /// Behind a TLS proxy the server only sees plain http (and maybe the proxy's own host name), so
    /// without this the Entra ID redirect would go to http://, and sign-in's cookies would not be
    /// marked Secure. The proxy is the only way in, so its public address is the true one. With an
    /// http:// public URL (local development) requests are left as they are.
    /// </summary>
    public static void UsePublicUrl(this WebApplication app, ServerOptions server)
    {
        var url = new Uri(server.PublicUrl);
        if (url.Scheme != Uri.UriSchemeHttps) return;
        var host = new HostString(url.Authority);
        app.Use((context, next) =>
        {
            context.Request.Scheme = Uri.UriSchemeHttps;
            context.Request.Host = host;
            return next(context);
        });
    }
}
