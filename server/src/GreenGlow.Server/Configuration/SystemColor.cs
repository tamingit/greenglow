using System.Text.RegularExpressions;

namespace GreenGlow.Server.Configuration;

/// <summary>A connection's colour on the page: #rrggbb, kept in lowercase.</summary>
public static partial class SystemColor
{
    /// <summary>The colour, or null when there is none or it isn't #rrggbb.</summary>
    public static string? Parse(string? text) =>
        text?.Trim() is { } color && Hex().IsMatch(color) ? color.ToLowerInvariant() : null;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();
}
