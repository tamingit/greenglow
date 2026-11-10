using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace GreenGlow.Server.Web;

/// <summary>Security headers on every response, and the built web app.</summary>
public static class StaticSite
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static void UseSecurityHeaders(this WebApplication app) =>
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            // same-origin, not no-referrer: under no-referrer browsers send "Origin: null" on
            // same-origin POSTs, and the Origin check would refuse them.
            headers["Referrer-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            return next(context);
        });

    /// <summary>
    /// Serves the built app. Vite's hashed files under /assets/ are cached for a year; everything
    /// else is revalidated so a new deployment shows up on the next load.
    /// </summary>
    public static void UseBuiltApp(this WebApplication app, string? staticDir)
    {
        if (string.IsNullOrEmpty(staticDir) || !Directory.Exists(staticDir)) return;
        var files = new PhysicalFileProvider(Path.GetFullPath(staticDir));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers[HeaderNames.CacheControl] =
                    context.Context.Request.Path.StartsWithSegments("/assets")
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        });
    }
}
