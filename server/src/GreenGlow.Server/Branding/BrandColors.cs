using System.Globalization;
using System.Text;

namespace GreenGlow.Server.Branding;

/// <summary>
/// /branding/brand.css: the page's frame in an administrator's brand colour. The colour is the
/// top bar's; everything else the frame uses (its edge, its menus, its text, its buttons, the
/// admin and help pages' accents, Night Shift's dimmer bar) is worked out from it, with text kept
/// readable on a dark or a light colour. The terminal's own colours (the looks, a user's theme)
/// are not touched.
/// </summary>
public static class BrandColors
{
    private readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb Parse(string hex) => new(
            int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber) / 255.0,
            int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber) / 255.0,
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber) / 255.0);

        public string Hex => $"#{Byte(R):x2}{Byte(G):x2}{Byte(B):x2}";

        public string Alpha(double a) =>
            $"rgb({Byte(R)} {Byte(G)} {Byte(B)} / {a.ToString("0.##", CultureInfo.InvariantCulture)})";

        private static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

        public Rgb Mix(Rgb other, double t) => new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t);

        public double Luminance
        {
            get
            {
                static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
            }
        }

        public double Contrast(Rgb other)
        {
            var (a, b) = (Luminance, other.Luminance);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        public (double H, double S, double L) Hsl
        {
            get
            {
                var max = Math.Max(R, Math.Max(G, B));
                var min = Math.Min(R, Math.Min(G, B));
                var l = (max + min) / 2;
                var d = max - min;
                if (d < 1e-9) return (0, 0, l);
                var s = d / (1 - Math.Abs(2 * l - 1));
                var h = max == R ? (G - B) / d % 6 : max == G ? (B - R) / d + 2 : (R - G) / d + 4;
                return ((h * 60 + 360) % 360, s, l);
            }
        }

        public static Rgb FromHsl(double h, double s, double l)
        {
            l = Math.Clamp(l, 0, 1);
            s = Math.Clamp(s, 0, 1);
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = l - c / 2;
            var (r, g, b) = (h % 360) switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };
            return new Rgb(r + m, g + m, b + m);
        }
    }

    private static readonly Rgb White = new(1, 1, 1);
    private static readonly Rgb Black = new(0, 0, 0);

    // The page backgrounds the accents sit on (style.css): light, the GreenGlow look's, dark, Night Shift's.
    private static readonly Rgb[] LightPages = [Rgb.Parse("#f7f8f5"), Rgb.Parse("#ffffff"), Rgb.Parse("#cddbca")];
    private static readonly Rgb[] DarkPages = [Rgb.Parse("#141712"), Rgb.Parse("#1c2019"), Rgb.Parse("#0e1310")];

    /// <summary>The stylesheet for a #rrggbb colour.</summary>
    public static string Css(string color)
    {
        var brand = Rgb.Parse(color);
        var (hue, saturation, _) = brand.Hsl;
        var tint = Math.Clamp(saturation, 0.3, 0.65);
        var lightAccent = Readable(hue, tint, 0.32, -0.02, LightPages);
        var darkAccent = Readable(hue, tint, 0.72, 0.02, DarkPages);
        var onDarkAccent = Rgb.FromHsl(hue, 0.55, 0.11);
        var onLightAccent = lightAccent.Contrast(White) >= 4.5 ? White : Rgb.FromHsl(hue, 0.55, 0.11);

        var css = new StringBuilder();
        css.Append(CultureInfo.InvariantCulture, $"/* The brand colour {color}, set on the admin page (Branding). */\n");
        // Repeated :root outranks style.css, admin.css and help.css whatever order the sheets load in.
        css.Append(":root:root:root {\n");
        Bar(css, brand);
        Accents(css, lightAccent, onLightAccent);
        css.Append("}\n\n@media (prefers-color-scheme: dark) {\n  :root:root:root:not([data-look='greenglow']) {\n");
        Accents(css, darkAccent, onDarkAccent, "  ");
        css.Append("  }\n}\n\n");
        // Night Shift dims the bar, as it does GreenGlow's green.
        css.Append(":root:root:root[data-look='night-shift'] {\n");
        Bar(css, brand.Mix(Black, 0.38));
        Accents(css, darkAccent, onDarkAccent);
        css.Append("}\n");
        return css.ToString();
    }

    private static void Bar(StringBuilder css, Rgb bg)
    {
        var (hue, saturation, _) = bg.Hsl;
        // White text unless the colour is too light for it.
        var lightText = bg.Contrast(White) >= 4.5 || bg.Contrast(White) >= bg.Contrast(Rgb.FromHsl(hue, 0.5, 0.1));
        var fg = lightText ? White : Rgb.FromHsl(hue, Math.Min(saturation, 0.5), 0.1);
        var tint = Math.Clamp(saturation, 0.35, 0.65);
        // The primary button: a light tint on a dark bar, a deep shade on a light one, always set apart from the bar.
        var primary = lightText ? Rgb.FromHsl(hue, tint, 0.72) : Rgb.FromHsl(hue, tint, 0.25);
        for (var step = 0; step < 10 && primary.Contrast(bg) < 3; step++)
        {
            var (_, s, l) = primary.Hsl;
            primary = Rgb.FromHsl(hue, s, lightText ? l + 0.03 : l - 0.03);
        }
        var (_, ps, pl) = primary.Hsl;
        var primaryHover = Rgb.FromHsl(hue, ps, lightText ? pl + 0.06 : pl - 0.05);
        var onPrimary = lightText ? Rgb.FromHsl(hue, 0.55, 0.11) : White;
        Line(css, "--bar-bg", bg.Hex);
        Line(css, "--bar-edge", bg.Mix(Black, 0.22).Hex);
        Line(css, "--bar-panel", bg.Mix(Black, 0.12).Hex);
        Line(css, "--bar-fg", fg.Hex);
        Line(css, "--bar-muted", fg.Mix(bg, 0.25).Hex);
        Line(css, "--bar-control", fg.Alpha(0.08));
        Line(css, "--bar-control-hover", fg.Alpha(0.16));
        Line(css, "--bar-line", fg.Alpha(0.22));
        Line(css, "--bar-primary", primary.Hex);
        Line(css, "--bar-primary-hover", primaryHover.Hex);
        Line(css, "--bar-on-primary", onPrimary.Hex);
        Line(css, "--dot-idle", fg.Mix(bg, 0.4).Hex);
    }

    private static void Accents(StringBuilder css, Rgb accent, Rgb onAccent, string indent = "")
    {
        Line(css, "--admin-accent", accent.Hex, indent);
        Line(css, "--admin-accent-fg", onAccent.Hex, indent);
        Line(css, "--admin-focus", accent.Hex, indent);
        Line(css, "--help-accent", accent.Hex, indent);
    }

    private static void Line(StringBuilder css, string token, string value, string indent = "") =>
        css.Append(CultureInfo.InvariantCulture, $"{indent}  {token}: {value};\n");

    /// <summary>The hue at the first lightness, moved by step until it reads (4.5:1) on every page background.</summary>
    private static Rgb Readable(double hue, double saturation, double lightness, double step, Rgb[] pages)
    {
        var color = Rgb.FromHsl(hue, saturation, lightness);
        for (var i = 0; i < 20 && pages.Any(page => color.Contrast(page) < 4.5); i++)
            color = Rgb.FromHsl(hue, saturation, lightness += step);
        return color;
    }
}
