using GreenGlow.Server.Auth;
using GreenGlow.Server.Branding;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/branding: the name, short name, brand colour and icon every page shows; and
/// /api/admin/address: the address people open GreenGlow at. Administrators only.
/// </summary>
public static class BrandingEndpoints
{
    /// <summary>A null or empty field goes back to GreenGlow's own.</summary>
    public sealed record BrandingRequest(string? Name, string? ShortName, string? Color);

    /// <summary>Every icon file (BrandIcons.Sizes), each a PNG as base64.</summary>
    public sealed record IconRequest(Dictionary<string, string>? Icons);

    /// <summary>The address; null goes back to Server__PublicUrl.</summary>
    public sealed record AddressRequest(string? Url);

    public static void MapBrandingEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/branding", (BrandStore brands, SettingsStore settings, ServerOptions server) =>
            Results.Json(Describe(brands.Current(), settings, server)));

        admin.MapPut("/branding", async (BrandingRequest request, BrandStore brands, SettingsStore settings, ServerOptions server) =>
        {
            var values = new Dictionary<string, string?>();
            if (Blank(request.Name)) values[BrandKeys.Name] = null;
            else if (BrandKeys.ParseName(request.Name, BrandKeys.MaxName) is { } name)
                values[BrandKeys.Name] = name == Brand.DefaultName ? null : name;
            else return ConnectionEndpoints.Problem("name", $"A name is up to {BrandKeys.MaxName} characters.");

            if (Blank(request.ShortName)) values[BrandKeys.ShortName] = null;
            else if (BrandKeys.ParseName(request.ShortName, BrandKeys.MaxShortName) is { } shortName)
                values[BrandKeys.ShortName] = shortName == Brand.DefaultShortName ? null : shortName;
            else return ConnectionEndpoints.Problem("shortName", $"A short name is up to {BrandKeys.MaxShortName} characters.");

            if (Blank(request.Color)) values[BrandKeys.Color] = null;
            else if (BrandKeys.ParseColor(request.Color) is { } color)
                values[BrandKeys.Color] = color == Brand.DefaultColor ? null : color;
            else return ConnectionEndpoints.Problem("color", "A colour is #rrggbb, such as #1d4d2c.");

            await brands.SaveAsync(values);
            return Results.Json(Describe(brands.Current(), settings, server));
        });

        admin.MapPut("/branding/icon", async (IconRequest request, BrandStore brands, SettingsStore settings, ServerOptions server) =>
        {
            var given = request.Icons ?? [];
            var values = new Dictionary<string, string?>();
            foreach (var (file, size) in BrandIcons.Sizes)
            {
                if (!given.TryGetValue(file, out var base64) || string.IsNullOrEmpty(base64))
                    return ConnectionEndpoints.Problem("icon", $"{file} is missing.");
                byte[] png;
                try
                {
                    png = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    return ConnectionEndpoints.Problem("icon", $"{file} isn't base64.");
                }
                if (BrandIcons.Problem(png, size) is { } problem) return ConnectionEndpoints.Problem("icon", $"{file} {problem}");
                values[BrandKeys.Icon(file)] = Convert.ToBase64String(png);
            }
            await brands.SaveAsync(values);
            return Results.Json(Describe(brands.Current(), settings, server));
        });

        admin.MapDelete("/branding/icon", async (BrandStore brands, SettingsStore settings, ServerOptions server) =>
        {
            await brands.SaveAsync(BrandIcons.Sizes.Keys.ToDictionary(BrandKeys.Icon, string? (_) => null));
            return Results.Json(Describe(brands.Current(), settings, server));
        });

        // Changing it changes which origin may open sessions, where share links point and, when
        // it is https://, the address every request is treated as arriving at (sign-in's redirect).
        admin.MapPut("/address", async (AddressRequest request, HttpContext context, SettingsStore settings, ServerOptions server,
            BrandStore brands, PasswordLogin.Services services) =>
        {
            string? url = null;
            if (!Blank(request.Url) && (url = PublicAddress.Parse(request.Url)) is null)
                return ConnectionEndpoints.Problem("url",
                    "The address is like http://localhost:8080 or https://greenglow.example.com, with a path only when the app is under one, such as https://example.com/greenglow. The path can't start with one of the app's own (api, auth, ws and the like).");
            var before = PublicAddress.Current(settings, server);
            await settings.SetTextAsync(new Dictionary<string, string?> { [PublicAddress.Setting] = url });
            var after = PublicAddress.Current(settings, server);
            if (after != before)
            {
                var me = CurrentUser.From(context.User)!;
                await PasswordLogin.LogAsync(context, services, me.Upn, me.Id, "", $"public address: {before} to {after}");
            }
            return Results.Json(Describe(brands.Current(), settings, server));
        });
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static object Describe(Brand brand, SettingsStore settings, ServerOptions server) => new
    {
        name = brand.NameOrDefault,
        shortName = brand.ShortNameOrDefault,
        color = brand.ColorOrDefault,
        icon = brand.HasIcon,
        version = brand.Version,
        defaults = new { name = Brand.DefaultName, shortName = Brand.DefaultShortName, color = Brand.DefaultColor },
        iconSizes = BrandIcons.Sizes,
        iconMaxBytes = BrandIcons.MaxBytes,
        address = new
        {
            current = PublicAddress.Current(settings, server),
            // Saved here or at setup; null when Server__PublicUrl is in force.
            saved = PublicAddress.Parse(settings.Text(PublicAddress.Setting)),
            configured = PublicAddress.Parse(server.PublicUrl) ?? server.PublicUrl,
        },
    };
}
