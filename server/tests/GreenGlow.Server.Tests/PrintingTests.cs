using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GreenGlow.Engine;
using GreenGlow.Server.Printing;

namespace GreenGlow.Server.Tests;

/// <summary>Spooled files as PDFs: SCS laid out as text pages, and the PDF they're written to.</summary>
public sealed partial class PrintingTests
{
    private static readonly byte[] NL = [0x15];
    private static readonly byte[] CR = [0x0D];
    private static readonly byte[] LF = [0x25];
    private static readonly byte[] FF = [0x0C];

    private static byte[] T(string text) => Ebcdic.Encode(text);
    private static byte[] Shf(byte columns) => [0x2B, 0xC1, 0x02, columns];
    private static byte[] Svf(byte lines) => [0x2B, 0xC2, 0x02, lines];
    private static byte[] Ahpp(byte column) => [0x34, 0xC0, column];
    private static byte[] Avpp(byte line) => [0x34, 0xC4, line];
    private static byte[] Scs(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static IEnumerable<string> Text(ScsPage page) =>
        page.Lines.Select(l => l.Layers.Count == 0 ? "" : l.Layers[0]);

    [Fact]
    public void TextAndNewLinesMakeLinesOnOnePage()
    {
        var document = Printing.Scs.Render(Scs(T("REPORT TITLE"), NL, T("  line two"), NL));
        var page = Assert.Single(document.Pages);
        Assert.Equal(["REPORT TITLE", "  line two"], Text(page));
    }

    [Fact]
    public void FormFeedStartsAPageButNotBeforeTheFirstOrAfterTheLast()
    {
        var document = Printing.Scs.Render(Scs(FF, T("page one"), NL, FF, T("page two"), NL, FF));
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(["page one"], Text(document.Pages[0]));
        Assert.Equal(["page two"], Text(document.Pages[1]));
    }

    [Fact]
    public void TwoFormFeedsInARowLeaveABlankPage()
    {
        var document = Printing.Scs.Render(Scs(T("one"), FF, FF, T("three")));
        Assert.Equal(3, document.Pages.Count);
        Assert.Empty(document.Pages[1].Lines);
    }

    [Fact]
    public void AbsolutePositionsPlaceTextAndAnEarlierLineMeansTheNextPage()
    {
        var document = Printing.Scs.Render(Scs(Avpp(3), Ahpp(5), T("at 3,5"), Avpp(2), Ahpp(1), T("next page")));
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(["", "", "    at 3,5"], Text(document.Pages[0]));
        Assert.Equal(["", "next page"], Text(document.Pages[1]));
    }

    [Fact]
    public void TheFormLengthOverflowsToANewPage()
    {
        var document = Printing.Scs.Render(Scs(Svf(2), T("a"), NL, T("b"), NL, T("c"), NL));
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(["a", "b"], Text(document.Pages[0]));
        Assert.Equal(["c"], Text(document.Pages[1]));
        Assert.Equal(2, document.Lines);
    }

    [Fact]
    public void CarriageReturnOverstrikesInLayersAndTheSameTextAddsNone()
    {
        var bold = Printing.Scs.Render(Scs(T("TOTAL"), CR, T("TOTAL"), NL));
        Assert.Equal(["TOTAL"], Assert.Single(bold.Pages).Lines[0].Layers);

        var underlined = Printing.Scs.Render(Scs(T("TOTAL  12"), CR, T("_____"), NL));
        Assert.Equal(["TOTAL  12", "_____"], Assert.Single(underlined.Pages).Lines[0].Layers);
    }

    [Fact]
    public void LineFeedKeepsTheColumn()
    {
        var document = Printing.Scs.Render(Scs(T("ab"), LF, T("cd")));
        Assert.Equal(["ab", "  cd"], Text(Assert.Single(document.Pages)));
    }

    [Fact]
    public void AWideFormSetsTheColumns()
    {
        var document = Printing.Scs.Render(Scs(Shf(198), Svf(66), T(new string('x', 150)), NL));
        Assert.Equal(198, document.Columns);
        Assert.Equal(66, document.Lines);

        var unset = Printing.Scs.Render(Scs(T(new string('y', 100)), NL));
        Assert.Equal(100, unset.Columns);
    }

    [Fact]
    public void DensitiesComeFromTheFirstLineAndCharacterDistance()
    {
        // 8 lines per inch is 9/72 inch; 15 characters per inch as the Toolbox writes it.
        var document = Printing.Scs.Render(Scs([0x2B, 0xC6, 0x02, 0x09], [0x2B, 0xD2, 0x04, 0x29, 0x00, 0x0F], T("x")));
        Assert.Equal(8, document.Lpi);
        Assert.Equal(15, document.Cpi);
    }

    [Fact]
    public void UnknownCommandsAndTransparentDataNeverPrint()
    {
        byte[] ppm = [0x2B, 0xD2, 0x0A, 0x48, 0, 0, 1, 1, 0, 1, 2, 0];
        byte[] unknown = [0x2B, 0xFE, 0x05, 0xC1, 0xC2, 0xC3, 0xC4];
        byte[] transparent = [0x35, 0x03, 0xC1, 0xC2, 0xC3];
        var document = Printing.Scs.Render(Scs(ppm, unknown, T("one"), transparent, T(" two"), [0x2F, 0x00, 0x09], NL));
        Assert.Equal(["one two"], Text(Assert.Single(document.Pages)));
    }

    [Fact]
    public void ATruncatedCommandAtTheEndIsIgnored()
    {
        var document = Printing.Scs.Render(Scs(T("end"), [0x2B, 0xC1]));
        Assert.Equal(["end"], Text(Assert.Single(document.Pages)));
    }

    [Fact]
    public void NothingPrintedIsOneEmptyPage()
    {
        var document = Printing.Scs.Render([]);
        Assert.Empty(Assert.Single(document.Pages).Lines);
        Assert.Single(Parse(Pdf.Write(document)).Pages);
    }

    [Fact]
    public void ThePdfIsWellFormedWithAPagePerSpooledPage()
    {
        var document = Printing.Scs.Render(Scs(T("first page"), FF, T("second (page) \\ ok"), FF, T("third")));
        var pdf = Parse(Pdf.Write(document, "QSYSPRT 12"));
        Assert.Equal(3, pdf.Pages.Count);
        Assert.Contains("/Count 3", pdf.Text);
        Assert.Contains("/MediaBox [0 0 612 792]", pdf.Pages[0].Dictionary);
        Assert.Contains(Hex("second (page) \\ ok"), pdf.Pages[1].Content);
        Assert.Contains(Hex("third"), pdf.Pages[2].Content);
        Assert.Contains("/Title <FEFF", pdf.Text);
    }

    [Fact]
    public void AReportWiderThan80ColumnsIsLandscapeAndFits()
    {
        var document = Printing.Scs.Render(Scs(Shf(132), Svf(66), T("x"), NL));
        var pdf = Parse(Pdf.Write(document));
        Assert.Contains("/MediaBox [0 0 792 612]", pdf.Pages[0].Dictionary);
        // 132 Courier characters at 0.6 em must fit in 792 - 36 points, and 66 lines in 612 - 36.
        var size = double.Parse(FontSize().Match(pdf.Pages[0].Content).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(132 * size * 0.6 <= 756.01, $"font size {size} is too wide");
        Assert.True(66 * size <= 576.01, $"font size {size} is too tall");
    }

    [Fact]
    public void CharactersOutsideLatin1PrintAsQuestionMarks()
    {
        var document = new ScsDocument([new ScsPage([new ScsLine(["a€b"])])], 80, 66, 10, 6);
        Assert.Contains(Hex("a?b"), Parse(Pdf.Write(document)).Pages[0].Content);
    }

    [Fact]
    public void ScsBecomesAPdfAndAPdfPassesThroughAndOthersCannotOpen()
    {
        var scs = SpooledPdf.From("*SCS", Scs(T("hello"), NL));
        Assert.NotNull(scs);
        Assert.True(SpooledPdf.IsPdf(scs));

        var pdf = "%PDF-1.7\nplaceholder"u8.ToArray();
        Assert.Same(pdf, SpooledPdf.From("*USERASCII", pdf));
        Assert.Null(SpooledPdf.From("*USERASCII", "\u001bE PCL"u8.ToArray()));
        Assert.Null(SpooledPdf.From("*AFPDS", [0x5A, 0x00]));
        Assert.False(SpooledPdf.CanOpen("*AFPDS"));
        Assert.True(SpooledPdf.CanOpen("*SCS"));
    }

    private static string Hex(string text) => Convert.ToHexString(Encoding.Latin1.GetBytes(text));

    [GeneratedRegex(@"/F1 ([\d.]+) Tf")]
    private static partial Regex FontSize();

    private sealed record ParsedPage(string Dictionary, string Content);

    private sealed record ParsedPdf(string Text, IReadOnlyList<ParsedPage> Pages);

    /// <summary>
    /// Checks the structure a viewer relies on (header, every cross-reference offset pointing
    /// at its object, the trailer and end marker) and returns each page with its inflated content.
    /// </summary>
    private static ParsedPdf Parse(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4\n", text);
        Assert.EndsWith("%%EOF\n", text);
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value);
        Assert.StartsWith("xref\n0 ", text[startxref..]);
        var xref = Regex.Match(text[startxref..], @"^xref\n0 (\d+)\n");
        var size = int.Parse(xref.Groups[1].Value);
        var entries = startxref + xref.Length;
        var objects = new Dictionary<int, string>();
        for (var n = 1; n < size; n++)
        {
            var entry = text.Substring(entries + 20 * n, 20);
            Assert.Matches(@"^\d{10} 00000 n \n$", entry);
            var offset = int.Parse(entry[..10]);
            Assert.StartsWith($"{n} 0 obj\n", text[offset..]);
            var end = text.IndexOf("\nendobj\n", offset, StringComparison.Ordinal);
            objects[n] = text[(offset + $"{n} 0 obj\n".Length)..end];
        }
        Assert.Contains($"/Size {size}", text);

        var pages = new List<ParsedPage>();
        var kids = Regex.Match(objects[2], @"/Kids \[([^\]]*)\]").Groups[1].Value;
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            var dictionary = objects[int.Parse(kid.Groups[1].Value)];
            var contents = int.Parse(Regex.Match(dictionary, @"/Contents (\d+) 0 R").Groups[1].Value);
            var stream = objects[contents];
            var length = int.Parse(Regex.Match(stream, @"/Length (\d+)").Groups[1].Value);
            var start = stream.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
            var data = Encoding.Latin1.GetBytes(stream.Substring(start, length));
            Assert.Equal("\nendstream", stream[(start + length)..]);
            using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            pages.Add(new ParsedPage(dictionary, reader.ReadToEnd()));
        }
        return new ParsedPdf(text, pages);
    }
}
