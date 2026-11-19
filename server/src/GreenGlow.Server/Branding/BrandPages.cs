using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace GreenGlow.Server.Branding;

/// <summary>
/// The brand on the served pages. With nothing set, the built pages are served as they are.
/// Otherwise each page (and the app manifest) is served with the brand's name, short name,
/// colour and icon in place of GreenGlow's, so it shows from the first paint, before any script
/// runs. The CSP allows no inline style, so the colour comes as /branding/brand.css; a custom
/// icon is served from /branding/ under the built icons' names.
/// </summary>
public static partial class BrandPages
{
    public const string Prefix = "/branding";

    /// <summary>The same, relative to a page or the manifest.</summary>
    private const string Relative = "branding";

    /// <summary>A built icon's address: ./icons/ in the build, /icons/ in the sources Vite serves.</summary>
    [GeneratedRegex("\"\\.?/icons/([a-z0-9-]+\\.png)\"")]
    private static partial Regex IconPath();

    /// <summary>
    /// A built page with the brand applied. The built pages carry GreenGlow's brand in three
    /// forms: its full name, its short name as a meta tag's content (the installed app's title),
    /// and its colour as the theme colour. packages/web/vite.config.ts does the same in development.
    /// </summary>
    public static string Html(string html, Brand brand)
    {
        var encode = HtmlEncoder.Default;
        if (brand.Name is { } name) html = html.Replace(Brand.DefaultName, encode.Encode(name), StringComparison.Ordinal);
        if (brand.ShortName is { } shortName)
            html = html.Replace($"content=\"{Brand.DefaultShortName}\"", $"content=\"{encode.Encode(shortName)}\"", StringComparison.Ordinal);
        if (brand.Color is { } color)
        {
            html = html.Replace($"content=\"{Brand.DefaultColor}\"", $"content=\"{color}\"", StringComparison.Ordinal);
            // Relative, as every address in the pages is: the app may be under a base path.
            var link = $"<link rel=\"stylesheet\" href=\"{Relative}/brand.css?v={brand.Version}\" />\n  </head>";
            html = ReplaceFirst(html, "</head>", link);
        }
        if (brand.HasIcon) html = IconPath().Replace(html, m => $"\"{Relative}/{m.Groups[1].Value}?v={brand.Version}\"");
        return html;
    }

    /// <summary>The built app manifest with the brand applied.</summary>
    public static string Manifest(string json, Brand brand)
    {
        if (JsonNode.Parse(json) is not JsonObject manifest) return json;
        if (brand.Name is { } name) manifest["name"] = name;
        if (brand.ShortName is { } shortName) manifest["short_name"] = shortName;
        if (brand.Color is { } color)
        {
            manifest["theme_color"] = color;
            manifest["background_color"] = color;
        }
        if (brand.HasIcon)
        {
            IEnumerable<JsonNode?> icons = manifest["icons"] as JsonArray ?? [];
            if (manifest["shortcuts"] is JsonArray shortcuts)
                icons = icons.Concat(shortcuts.SelectMany(s => s?["icons"] as JsonArray ?? []));
            foreach (var icon in icons.OfType<JsonObject>())
                if (icon["src"]?.GetValue<string>() is { } src && src.TrimStart('.', '/').StartsWith("icons/", StringComparison.Ordinal))
                    icon["src"] = $"{Relative}/{src.TrimStart('.', '/')["icons/".Length..]}?v={brand.Version}";
        }
        return manifest.ToJsonString();
    }

    private static string ReplaceFirst(string text, string find, string with)
    {
        var at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? text : string.Concat(text.AsSpan(0, at), with, text.AsSpan(at + find.Length));
    }

    /// <summary>
    /// Serves the built pages and the manifest with the brand applied, when there is one. Goes
    /// before the built app's static files, which serve them otherwise.
    /// </summary>
    public static void UseBrandedPages(this WebApplication app, string? staticDir)
    {
        if (string.IsNullOrEmpty(staticDir) || !Directory.Exists(staticDir)) return;
        var files = new PhysicalFileProvider(Path.GetFullPath(staticDir));
        var cache = new ConcurrentDictionary<string, (DateTimeOffset Modified, string Version, byte[] Body)>();
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            {
                await next(context);
                return;
            }
            var path = request.Path.Value ?? "";
            if (path == "/") path = "/index.html";
            var isPage = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
            var isManifest = path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase);
            var brand = isPage || isManifest ? context.RequestServices.GetRequiredService<BrandStore>().Current() : null;
            var file = brand is { IsCustom: true } ? files.GetFileInfo(path) : null;
            if (file is not { Exists: true, IsDirectory: false })
            {
                await next(context);
                return;
            }
            var key = path.ToLowerInvariant();
            if (!cache.TryGetValue(key, out var hit) || hit.Modified != file.LastModified || hit.Version != brand!.Version)
            {
                string text;
                await using (var stream = file.CreateReadStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = await reader.ReadToEndAsync(context.RequestAborted);
                var body = Encoding.UTF8.GetBytes(isPage ? Html(text, brand!) : Manifest(text, brand!));
                cache[key] = hit = (file.LastModified, brand!.Version, body);
            }
            var response = context.Response;
            response.ContentType = isPage ? "text/html; charset=utf-8" : "application/manifest+json";
            response.Headers[HeaderNames.CacheControl] = "no-cache";
            response.ContentLength = hit.Body.Length;
            if (HttpMethods.IsGet(request.Method)) await response.Body.WriteAsync(hit.Body, context.RequestAborted);
        });
    }

    /// <summary>
    /// /branding/: the colours and a custom icon's files. For anyone, signed in or not: the
    /// sign-in page shows them too. Pages link them with the brand's version in the address, so a
    /// change shows at the next load.
    /// </summary>
    public static void MapBrandingFiles(this WebApplication app)
    {
        var branding = app.MapGroup(Prefix);
        branding.MapGet("/brand.css", (HttpContext context, BrandStore store) =>
        {
            var brand = store.Current();
            Revalidate(context);
            return Results.Text(brand.Color is { } color ? BrandColors.Css(color) : "/* GreenGlow's own colours. */\n", "text/css");
        });
        branding.MapGet("/brand.json", (HttpContext context, BrandStore store) =>
        {
            // For packages/web/vite.config.ts, which brands the pages Vite serves in development.
            var brand = store.Current();
            Revalidate(context);
            return Results.Json(new { name = brand.Name, shortName = brand.ShortName, color = brand.Color, icon = brand.HasIcon, version = brand.Version });
        });
        branding.MapGet("/{file}", (string file, HttpContext context, BrandStore store) =>
        {
            if (!BrandIcons.Sizes.ContainsKey(file)) return Results.NotFound();
            Revalidate(context);
            // No custom icon (any more): the built one.
            return store.Icon(file) is { } png ? Results.Bytes(png, "image/png") : Results.Redirect(Web.PublicAddress.Here(context, $"/icons/{file}"));
        });
    }

    private static void Revalidate(HttpContext context) => context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
}
