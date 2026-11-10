using System.Text.RegularExpressions;
using GreenGlow.Server.Auth;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/guide: the administrator guide (Help/admin-guide.html) and its screenshots, for
/// administrators only. They are built into the server rather than the web app, so no one else
/// can load them; the help page shows the guide to administrators.
/// </summary>
public static partial class GuideEndpoints
{
    private const string Prefix = "Help/";

    [GeneratedRegex("^[a-z0-9-]{1,64}\\.png$")]
    private static partial Regex ImageName();

    public static RouteGroupBuilder MapGuideEndpoints(this RouteGroupBuilder api)
    {
        var guide = api.MapGroup("/admin/guide").RequireAuthorization(AuthSetup.AdminPolicy);
        guide.MapGet("", (HttpContext context) => Resource(context, "admin-guide.html", "text/html; charset=utf-8"));
        guide.MapGet("/images/{name}", (string name, HttpContext context) =>
            ImageName().IsMatch(name) ? Resource(context, name, "image/png") : Results.NotFound());
        return api;
    }

    private static IResult Resource(HttpContext context, string name, string contentType)
    {
        var stream = typeof(GuideEndpoints).Assembly.GetManifestResourceStream(Prefix + name);
        if (stream is null) return Results.NotFound();
        // Administrators only: never kept by a shared cache.
        context.Response.Headers.CacheControl = "private, no-cache";
        return Results.Stream(stream, contentType);
    }
}
