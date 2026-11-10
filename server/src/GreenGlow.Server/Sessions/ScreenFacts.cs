using System.Text.RegularExpressions;
using GreenGlow.Engine;

namespace GreenGlow.Server.Sessions;

/// <summary>
/// What the session list shows about a screen, read from the stream alone: its title, and
/// whether the user is past the sign-on screen and as whom.
/// </summary>
public static partial class ScreenFacts
{
    private const int MaxTitle = 80;

    /// <summary>
    /// The screen's title: on row 1, the high-intensity text, else the centred text, else the
    /// only wordy segment (the same rules as the browser's header band). Failing that, the
    /// first row with text. A program ID at the start of row 1 is skipped.
    /// </summary>
    public static string Title(ScreenSnapshot snapshot)
    {
        var lines = Display.ScreenText(snapshot);
        if (lines.Count == 0) return "";
        var parts = Segments(lines[0]);
        if (parts.Count > 0 && parts[0].Column <= 4 && IdPattern().IsMatch(parts[0].Text) && IdShape(parts[0].Text, parts.Count > 1))
            parts = parts[1..];
        var title = PickTitle(snapshot, parts)?.Text;
        title ??= lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        title = WhiteSpace().Replace(title, " ");
        return title.Length > MaxTitle ? title[..MaxTitle] : title;
    }

    private sealed record Segment(int Column, string Text);

    [GeneratedRegex(@"^[A-Za-z@#$][\w@#$]{0,9}(?:/[A-Za-z@#$][\w@#$]{0,9})?$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\d{1,2}[:/]\d{2}")]
    private static partial Regex DateOrTime();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    private static bool IdShape(string text, bool hasOtherText)
    {
        if (text.Contains('/') || text.Any(char.IsAsciiDigit) || Regex.IsMatch(text, "[a-z][A-Z]")) return true;
        return hasOtherText && Regex.IsMatch(text, "^[A-Z@#$][A-Z0-9@#$_]{2,}$");
    }

    private static Segment? PickTitle(ScreenSnapshot snapshot, IReadOnlyList<Segment> parts)
    {
        var bright = parts.Where(p => AllHighIntensity(snapshot, p)).ToList();
        if (bright.Count > 0) return bright.MaxBy(p => p.Text.Length);
        var lettered = parts.Where(p => p.Text.Any(char.IsAsciiLetter)).ToList();
        var centre = (snapshot.Columns + 1) / 2.0;
        var centred = lettered
            .Where(p => Math.Abs(p.Column + (p.Text.Length - 1) / 2.0 - centre) <= snapshot.Columns * 0.15).ToList();
        if (centred.Count == 1) return centred[0];
        var wordy = lettered
            .Where(p => Regex.Matches(p.Text, "[A-Za-z]{2,}").Count >= 2 && !DateOrTime().IsMatch(p.Text)).ToList();
        return wordy.Count == 1 ? wordy[0] : null;
    }

    private static bool AllHighIntensity(ScreenSnapshot snapshot, Segment part)
    {
        for (var i = 0; i < part.Text.Length; i++)
        {
            if (part.Text[i] == ' ') continue;
            var attribute = snapshot.Attributes[part.Column - 1 + i];
            if (attribute is not (0x22 or 0x23 or 0x26)) return false;
        }
        return true;
    }

    /// <summary>A row split into segments at two or more spaces.</summary>
    private static List<Segment> Segments(string line)
    {
        var parts = new List<Segment>();
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            if (i < line.Length && line[i] != ' ')
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;
            if (i + 1 < line.Length && line[i + 1] != ' ') continue;
            parts.Add(new Segment(start + 1, line[start..i]));
            start = -1;
        }
        return parts;
    }

    /// <summary>
    /// The snapshot as browsers get it: whatever was typed into nondisplay cells (passwords)
    /// becomes a placeholder, so the browser can still show one bullet per character typed but
    /// never learns the characters. A shared view-only browser must not see a password.
    /// </summary>
    public static ScreenSnapshot Redact(ScreenSnapshot snapshot)
    {
        byte[]? buffer = null;
        for (var i = 0; i < snapshot.Buffer.Length; i++)
        {
            var b = snapshot.Buffer[i];
            if (!Display.IsNondisplay(snapshot.Attributes[i]) || Display.IsAttribute(b) || b is 0x00 or 0x40 or Placeholder) continue;
            buffer ??= (byte[])snapshot.Buffer.Clone();
            buffer[i] = Placeholder;
        }
        return buffer is null ? snapshot : snapshot with { Buffer = buffer };
    }

    /// <summary>EBCDIC '*': what a typed nondisplay character becomes in a redacted snapshot.</summary>
    public const byte Placeholder = 0x5c;

    /// <summary>The screen size and where its input fields are; not their contents.</summary>
    public static string Layout(ScreenSnapshot snapshot) =>
        string.Join(' ', snapshot.Fields.Select(f => $"{f.Row}:{f.Column}:{f.Length}").Prepend($"{snapshot.Rows}x{snapshot.Columns}"));
}

/// <summary>
/// Tells the sign-on screen from the screens after it. The first screen of a session that
/// takes input is the sign-on screen, because nothing signs on automatically. A failed sign-on
/// shows that screen again with the same fields, so the user counts as signed on while the
/// screen's input fields differ from the first screen's.
///
/// The IBM i user is what was typed in the sign-on screen's first displayed input field (the
/// User field) when an AID key left it. Nondisplay fields (the password) are never read.
/// </summary>
public sealed class SignOnWatch
{
    private string? _signOnLayout;
    private string? _candidate;

    public bool SignedOn { get; private set; }
    public string? IbmUser { get; private set; }

    public void See(ScreenSnapshot snapshot)
    {
        if (_signOnLayout is null)
        {
            if (snapshot.Keyboard != KeyboardState.Locked && snapshot.Fields.Count > 0)
                _signOnLayout = ScreenFacts.Layout(snapshot);
            return;
        }
        var signedOn = ScreenFacts.Layout(snapshot) != _signOnLayout;
        if (signedOn && !SignedOn && _candidate is not null) IbmUser = _candidate;
        if (!signedOn && SignedOn && snapshot.Keyboard != KeyboardState.Locked) IbmUser = null;
        SignedOn = signedOn;
    }

    /// <summary>Call with the screen as it is just before an AID key is sent.</summary>
    public void BeforeAid(ScreenSnapshot snapshot)
    {
        if (_signOnLayout is null || ScreenFacts.Layout(snapshot) != _signOnLayout) return;
        var user = snapshot.Fields.FirstOrDefault(f => !Display.IsNondisplay(f.Attribute) && !f.Format.Bypass);
        if (user is null) return;
        var text = Display.FieldText(snapshot, user).Trim().ToUpperInvariant();
        _candidate = text.Length is > 0 and <= 10 ? text : null;
    }
}
