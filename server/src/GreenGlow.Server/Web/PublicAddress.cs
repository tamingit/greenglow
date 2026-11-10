using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.Web;

public static class PublicAddress
{
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
