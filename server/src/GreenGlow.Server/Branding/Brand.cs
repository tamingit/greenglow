using System.Buffers.Binary;
using System.Text.RegularExpressions;
using GreenGlow.Server.Data;

namespace GreenGlow.Server.Branding;

/// <summary>
/// The product's name, short name, brand colour and icon, as an administrator set them on the
/// admin page (Branding). Kept as settings, so they sync to every node like the others. Nothing
/// set means GreenGlow Connect as built: the pages are then served exactly as they are.
/// </summary>
public sealed record Brand(string? Name, string? ShortName, string? Color, bool HasIcon, string Version)
{
    public const string DefaultName = "GreenGlow Connect";
    public const string DefaultShortName = "GreenGlow";

    /// <summary>The top bar's forest green, and the browser's theme colour.</summary>
    public const string DefaultColor = "#1d4d2c";

    public string NameOrDefault => Name ?? DefaultName;
    public string ShortNameOrDefault => ShortName ?? DefaultShortName;
    public string ColorOrDefault => Color ?? DefaultColor;

    /// <summary>Whether anything differs from the built pages.</summary>
    public bool IsCustom => Name is not null || ShortName is not null || Color is not null || HasIcon;
}

public static partial class BrandKeys
{
    public const string Name = "Brand.Name";
    public const string ShortName = "Brand.ShortName";
    public const string Color = "Brand.Color";

    /// <summary>Changes with every branding change: it busts the browser's copy of the icons and colours.</summary>
    public const string Version = "Brand.Version";

    /// <summary>One icon file (a PNG, as base64): Brand.Icon.icon-192.png and so on.</summary>
    public static string Icon(string file) => "Brand.Icon." + file;

    public const int MaxName = 60;
    public const int MaxShortName = 20;

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>A colour as #rrggbb in lower case, or null when it isn't one.</summary>
    public static string? ParseColor(string? value) =>
        value?.Trim().ToLowerInvariant() is { } color && ColorPattern().IsMatch(color) ? color : null;

    /// <summary>A name with no control characters and at most max characters, trimmed; null when it isn't one.</summary>
    public static string? ParseName(string? value, int max) =>
        value?.Trim() is { Length: > 0 } name && name.Length <= max && !name.Any(char.IsControl) ? name : null;
}

/// <summary>
/// The icon files a custom icon is made of: the same names and sizes as the built ones in
/// /icons/, so the pages and the manifest only change their folder. The admin page draws them
/// from the image an administrator chose; the server only checks them.
/// </summary>
public static class BrandIcons
{
    public static readonly IReadOnlyDictionary<string, int> Sizes = new Dictionary<string, int>
    {
        ["favicon-32.png"] = 32,
        ["apple-touch-icon.png"] = 180,
        ["icon-192.png"] = 192,
        ["icon-512.png"] = 512,
        ["icon-maskable-512.png"] = 512,
    };

    /// <summary>The most one icon file may be: they travel to every node and every browser.</summary>
    public const int MaxBytes = 384 * 1024;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>Why bytes aren't a PNG of size by size within the limit, or null when they are.</summary>
    public static string? Problem(byte[] png, int size)
    {
        if (png.Length > MaxBytes) return $"is {png.Length / 1024} KB; the most is {MaxBytes / 1024} KB. Try a simpler image.";
        // The signature, then the IHDR chunk: length 13, type, width, height.
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return "isn't a PNG.";
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        return width == size && height == size ? null : $"is {width}×{height}, not {size}×{size}.";
    }
}

/// <summary>The brand in force, read from the settings (which are cached).</summary>
public sealed class BrandStore(SettingsStore settings)
{
    public Brand Current()
    {
        var hasIcon = BrandIcons.Sizes.Keys.All(file => settings.Text(BrandKeys.Icon(file)) is not null);
        return new Brand(
            BrandKeys.ParseName(settings.Text(BrandKeys.Name), BrandKeys.MaxName),
            BrandKeys.ParseName(settings.Text(BrandKeys.ShortName), BrandKeys.MaxShortName),
            BrandKeys.ParseColor(settings.Text(BrandKeys.Color)),
            hasIcon,
            settings.Text(BrandKeys.Version) ?? "0");
    }

    /// <summary>A custom icon file, or null when there is none.</summary>
    public byte[]? Icon(string file)
    {
        if (!BrandIcons.Sizes.ContainsKey(file) || settings.Text(BrandKeys.Icon(file)) is not { } text) return null;
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Saves text settings with a new version, so browsers fetch the brand again.</summary>
    public Task SaveAsync(Dictionary<string, string?> values)
    {
        values[BrandKeys.Version] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().AsSpan(0, 6));
        return settings.SetTextAsync(values);
    }
}
