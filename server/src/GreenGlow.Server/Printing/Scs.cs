using System.Text;
using GreenGlow.Engine;

namespace GreenGlow.Server.Printing;

/// <summary>
/// One printed line: the text laid down first, then any text struck over it (a carriage return
/// and more text, as reports do for underlines and bold), each as its own layer.
/// </summary>
public sealed record ScsLine(IReadOnlyList<string> Layers)
{
    public static readonly ScsLine Blank = new([]);
}

/// <summary>A page: line 1 first, blank lines kept so positions are right.</summary>
public sealed record ScsPage(IReadOnlyList<ScsLine> Lines);

/// <summary>
/// A spooled file laid out as text pages. Columns and Lines are the widest and longest page:
/// the forms size the file asked for (Set Horizontal and Vertical Format), or more if text went
/// past it. Cpi and Lpi are the densities it asked for first.
/// </summary>
public sealed record ScsDocument(IReadOnlyList<ScsPage> Pages, int Columns, int Lines, double Cpi, double Lpi);

/// <summary>
/// Lays out SCS (SNA Character String) spooled data as text pages, the way a line printer
/// would: EBCDIC CCSID 37 graphics, NL, CR, LF, FF, absolute and relative positioning, and the
/// forms and density commands. Every other command is framed and skipped, so unknown ones never
/// print. Text only: no fonts, overlays or images. Byte values are from IBM's SCS writers in
/// the Toolbox for Java (SCS5256Writer, SCS5224Writer, SCS5219Writer).
/// </summary>
public static class Scs
{
    private const int DefaultLines = 66;
    private const int DefaultColumns = 80;

    // Single-byte controls.
    private const byte Null = 0x00;
    private const byte HorizontalTab = 0x05;
    private const byte RequiredNewLine = 0x06;
    private const byte GraphicEscape = 0x08;
    private const byte VerticalTab = 0x0B;
    private const byte FormFeed = 0x0C;
    private const byte CarriageReturn = 0x0D;
    private const byte NewLine = 0x15;
    private const byte Backspace = 0x16;
    private const byte RecordSeparator = 0x1E;
    private const byte LineFeed = 0x25;
    private const byte Command = 0x2B;
    private const byte Position = 0x34;
    private const byte Transparent = 0x35;

    // 2B commands: 2B, class, then a length byte that counts itself and the parameters.
    private const byte SetHorizontalFormat = 0xC1;
    private const byte SetVerticalFormat = 0xC2;
    private const byte SetLineDensity = 0xC6;
    private const byte PageClass = 0xD2;
    private const byte SetCharacterDistance = 0x29;

    // 34 commands: 34, kind, one parameter.
    private const byte AbsoluteHorizontal = 0xC0;
    private const byte AbsoluteVertical = 0xC4;
    private const byte RelativeHorizontal = 0xC8;
    private const byte RelativeVertical = 0x4C;

    public static ScsDocument Render(ReadOnlySpan<byte> data)
    {
        var layout = new Layout();
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (b >= 0x40)
            {
                layout.Print(Ebcdic.ToChar(b));
                i++;
                continue;
            }
            switch (b)
            {
                case NewLine or RequiredNewLine or RecordSeparator:
                    layout.NewLine();
                    i++;
                    break;
                case CarriageReturn:
                    layout.Column = 1;
                    i++;
                    break;
                case LineFeed or VerticalTab:
                    layout.Down(1);
                    i++;
                    break;
                case FormFeed:
                    layout.FormFeed();
                    i++;
                    break;
                case Backspace:
                    layout.Column = Math.Max(1, layout.Column - 1);
                    i++;
                    break;
                case HorizontalTab:
                    layout.Tab();
                    i++;
                    break;
                case GraphicEscape:
                    if (i + 1 < data.Length) layout.Print(Ebcdic.ToChar(data[i + 1]));
                    i += 2;
                    break;
                case Position:
                    if (i + 2 < data.Length) layout.Move(data[i + 1], data[i + 2]);
                    i += 3;
                    break;
                case Transparent:
                    // Printer-specific bytes sent as they are: nothing a text page can show.
                    i += 2 + (i + 1 < data.Length ? data[i + 1] : 0);
                    break;
                case Command:
                {
                    if (i + 2 >= data.Length)
                    {
                        i = data.Length;
                        break;
                    }
                    var length = Math.Max((int)data[i + 2], 1);
                    var end = Math.Min(i + 2 + length, data.Length);
                    layout.Apply(data[i + 1], data[(i + 3)..end]);
                    i = end;
                    break;
                }
                default:
                    // NUL, superscript, subscript, bell and the rest: no mark on the page.
                    i++;
                    break;
            }
        }
        return layout.Finish();
    }

    private sealed class Layout
    {
        private readonly List<ScsPage> _pages = [];
        private SortedDictionary<int, List<StringBuilder>> _page = [];
        private bool _pageTouched;
        private int _formLines = DefaultLines;
        private int _formColumns;
        private int[] _tabs = [];
        private int _columns;
        private int _lines;
        private double _cpi = 10;
        private double _lpi = 6;
        private bool _cpiSet;
        private bool _lpiSet;

        public int Column { get; set; } = 1;
        private int _line = 1;

        public void Print(char c)
        {
            _pageTouched = true;
            if (c != ' ' && !char.IsControl(c)) Place(c);
            Column++;
        }

        /// <summary>The first layer with room takes the character; the same character struck again adds nothing.</summary>
        private void Place(char c)
        {
            if (!_page.TryGetValue(_line, out var layers)) _page[_line] = layers = [];
            var index = Column - 1;
            foreach (var layer in layers)
            {
                if (layer.Length <= index) layer.Append(' ', index + 1 - layer.Length);
                if (layer[index] == c) return;
                if (layer[index] != ' ') continue;
                layer[index] = c;
                return;
            }
            var added = new StringBuilder().Append(' ', index).Append(c);
            layers.Add(added);
        }

        public void NewLine()
        {
            Column = 1;
            Down(1);
        }

        public void Down(int lines)
        {
            _pageTouched = true;
            _line += lines;
            if (_line > _formLines)
            {
                NextPage();
                _line = 1;
            }
        }

        public void FormFeed()
        {
            // A form feed before anything is printed is the printer getting to the top of the
            // first form, not a blank page.
            if (_pages.Count == 0 && !_pageTouched) return;
            NextPage();
            _line = 1;
            Column = 1;
        }

        public void Tab()
        {
            var next = _tabs.FirstOrDefault(t => t > Column);
            Column = next > 0 ? next : (Column - 1) / 8 * 8 + 9;
        }

        public void Move(byte kind, byte value)
        {
            if (value == 0) return;
            switch (kind)
            {
                case AbsoluteHorizontal:
                    Column = value;
                    break;
                case RelativeHorizontal:
                    Column += value;
                    break;
                case AbsoluteVertical:
                    // Above the current line means that line on the next page.
                    if (value < _line) NextPage();
                    _pageTouched = true;
                    _line = value;
                    break;
                case RelativeVertical:
                    Down(value);
                    break;
            }
        }

        public void Apply(byte kind, ReadOnlySpan<byte> parameters)
        {
            switch (kind)
            {
                case SetHorizontalFormat when parameters.Length > 0:
                    if (parameters[0] > 0)
                    {
                        _formColumns = parameters[0];
                        _columns = Math.Max(_columns, _formColumns);
                    }
                    _tabs = parameters[1..].ToArray().Where(t => t > 0).Select(t => (int)t).Order().ToArray();
                    break;
                case SetVerticalFormat when parameters.Length > 0:
                    // The printer counts lines from 1 again on the new form length.
                    if (parameters[0] > 0)
                    {
                        _formLines = parameters[0];
                        _lines = Math.Max(_lines, _formLines);
                    }
                    _line = 1;
                    break;
                case SetLineDensity when parameters.Length > 0 && parameters[0] > 0:
                    // Line spacing in 1/72 inch.
                    if (!_lpiSet) _lpi = 72.0 / parameters[0];
                    _lpiSet = true;
                    break;
                case PageClass when parameters.Length >= 3 && parameters[0] == SetCharacterDistance:
                {
                    var distance = parameters[1] << 8 | parameters[2];
                    // Characters per inch as the Toolbox writes it (10, 12, 15), or 1440ths of an inch.
                    var cpi = distance switch { 0 => 0, <= 30 => distance, _ => 1440.0 / distance };
                    if (cpi > 0 && !_cpiSet) _cpi = cpi;
                    if (cpi > 0) _cpiSet = true;
                    break;
                }
            }
        }

        private void NextPage()
        {
            _pages.Add(Freeze());
            _page = [];
            _pageTouched = false;
        }

        private ScsPage Freeze()
        {
            if (_page.Count == 0) return new ScsPage([]);
            var last = _page.Keys.Max();
            _lines = Math.Max(_lines, last);
            var lines = new ScsLine[last];
            for (var n = 1; n <= last; n++)
            {
                if (!_page.TryGetValue(n, out var layers))
                {
                    lines[n - 1] = ScsLine.Blank;
                    continue;
                }
                var texts = layers.Select(l => l.ToString().TrimEnd()).Where(t => t.Length > 0).ToList();
                _columns = Math.Max(_columns, texts.Select(t => t.Length).DefaultIfEmpty(0).Max());
                lines[n - 1] = new ScsLine(texts);
            }
            return new ScsPage(lines);
        }

        public ScsDocument Finish()
        {
            var pages = new List<ScsPage>(_pages) { Freeze() };
            // A form feed after the last line ends that page; it doesn't start a blank one.
            while (pages.Count > 1 && pages[^1].Lines.Count == 0) pages.RemoveAt(pages.Count - 1);
            var columns = _columns > 0 ? _columns : _formColumns > 0 ? _formColumns : DefaultColumns;
            var lines = Math.Max(_lines, 1);
            return new ScsDocument(pages, columns, lines, _cpi, _lpi);
        }
    }
}
