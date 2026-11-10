using System.Text;
using GreenGlow.Engine;

namespace GreenGlow.Server.Sessions;

/// <summary>
/// Pasted text as the keys a user would type for it, as ACS pastes: characters from the cursor,
/// each further line from the same column one row down, and a tab as Tab (the next input field,
/// so a spreadsheet row fills a row of fields). Never Enter or any other key that sends: the
/// user presses Enter when they've checked what was pasted.
/// </summary>
public static class Paste
{
    /// <summary>More than a 27 x 132 screen holds.</summary>
    public const int MaxCharacters = 4000;

    /// <summary>The keys, and whether lines were left out because they'd fall below the screen.</summary>
    public static (List<Key> Keys, bool PastScreen) Keys(string text, Position cursor, int rows)
    {
        var keys = new List<Key>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A copied block usually ends in a newline; it isn't one more line to paste.
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                if (cursor.Row + i > rows) return (keys, true);
                keys.Add(new CursorKey(cursor.Row + i, cursor.Column));
            }
            foreach (var rune in lines[i].EnumerateRunes())
            {
                if (rune.Value == '\t') keys.Add(new FunctionKey("Tab"));
                else if (!Rune.IsControl(rune)) keys.Add(new CharacterKey(rune.ToString()));
            }
        }
        return (keys, false);
    }
}
