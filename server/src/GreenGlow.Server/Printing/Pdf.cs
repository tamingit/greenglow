using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GreenGlow.Server.Printing;

/// <summary>
/// Writes text pages as a PDF: Courier (a standard font every viewer has, so nothing is
/// embedded) on US Letter, landscape when the report is wider than 80 columns, the type scaled
/// down only as far as the widest line and the longest page need. Struck-over layers are drawn
/// in the same place, as the printer would. Our own small writer: plain objects, a compressed
/// content stream per page and a cross-reference table.
/// </summary>
public static class Pdf
{
    private const double LetterShort = 612;
    private const double LetterLong = 792;
    private const double Margin = 18;
    private const double CourierAdvance = 0.6;

    public static byte[] Write(ScsDocument document, string? title = null)
    {
        var landscape = document.Columns > 80;
        var width = landscape ? LetterLong : LetterShort;
        var height = landscape ? LetterShort : LetterLong;
        var advance = Math.Min(72 / document.Cpi, (width - 2 * Margin) / document.Columns);
        var leading = Math.Min(72 / document.Lpi, (height - 2 * Margin) / document.Lines);
        // Rounded down, as it is written with two decimals, so the page still fits.
        var size = Math.Floor(Math.Min(advance / CourierAdvance, leading) * 100) / 100;

        var writer = new Writer();
        var pageCount = Math.Max(document.Pages.Count, 1);
        const int catalog = 1, pages = 2, font = 3, info = 4, first = 5;
        writer.Object(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(p => $"{first + 2 * p} 0 R"));
        writer.Object(pages, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        writer.Object(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        writer.Object(info, title is null ? "<< /Producer (GreenGlow) >>" : $"<< /Producer (GreenGlow) /Title {Utf16(title)} >>");
        for (var p = 0; p < pageCount; p++)
        {
            var page = p < document.Pages.Count ? document.Pages[p] : new ScsPage([]);
            var content = Compress(Content(page, height, size, leading));
            writer.Object(first + 2 * p,
                $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {N(width)} {N(height)}] " +
                $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {first + 2 * p + 1} 0 R >>");
            writer.Stream(first + 2 * p + 1, $"/Filter /FlateDecode /Length {content.Length}", content);
        }
        return writer.Finish(catalog, info);
    }

    private static byte[] Content(ScsPage page, double height, double size, double leading)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"BT /F1 {N(size)} Tf\n");
        for (var n = 0; n < page.Lines.Count; n++)
        {
            // The baseline sits a fifth of the line above its bottom, leaving room for descenders.
            var baseline = height - Margin - (n + 1) * leading + leading * 0.2;
            foreach (var layer in page.Lines[n].Layers)
                text.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {N(Margin)} {N(baseline)} Tm <{WinAnsiHex(layer)}> Tj\n");
        }
        text.Append("ET\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>WinAnsiEncoding matches Latin-1 for everything CCSID 37 can hold; anything else prints as ?.</summary>
    private static string WinAnsiHex(string text)
    {
        var hex = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            var b = c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' ? (byte)c : (byte)'?';
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return hex.ToString();
    }

    private static string Utf16(string text)
    {
        var hex = new StringBuilder("<FEFF");
        foreach (var c in text) hex.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return hex.Append('>').ToString();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal)) zlib.Write(data);
        return output.ToArray();
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed class Writer
    {
        private readonly MemoryStream _out = new();
        private readonly SortedDictionary<int, long> _offsets = [];

        public Writer()
        {
            Raw("%PDF-1.4\n");
            // A comment of high bytes marks the file as binary for anything that guesses.
            _out.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        }

        public void Object(int number, string body)
        {
            _offsets[number] = _out.Position;
            Raw($"{number} 0 obj\n{body}\nendobj\n");
        }

        public void Stream(int number, string dictionary, byte[] data)
        {
            _offsets[number] = _out.Position;
            Raw($"{number} 0 obj\n<< {dictionary} >>\nstream\n");
            _out.Write(data);
            Raw("\nendstream\nendobj\n");
        }

        public byte[] Finish(int root, int info)
        {
            var xref = _out.Position;
            var size = _offsets.Keys.Max() + 1;
            var table = new StringBuilder();
            table.Append(CultureInfo.InvariantCulture, $"xref\n0 {size}\n0000000000 65535 f \n");
            for (var n = 1; n < size; n++)
                table.Append(CultureInfo.InvariantCulture, $"{_offsets[n]:D10} 00000 n \n");
            table.Append(CultureInfo.InvariantCulture,
                $"trailer\n<< /Size {size} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Raw(table.ToString());
            return _out.ToArray();
        }

        private void Raw(string text) => _out.Write(Encoding.ASCII.GetBytes(text));
    }
}
