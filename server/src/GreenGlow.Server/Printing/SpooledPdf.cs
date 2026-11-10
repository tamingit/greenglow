namespace GreenGlow.Server.Printing;

/// <summary>
/// A spooled file as a PDF, by its printer device type: *SCS is laid out as
/// text pages, *USERASCII that is already a PDF is passed through, and anything else (AFP, IPDS,
/// line data, other printer languages) can't be shown here.
/// </summary>
public static class SpooledPdf
{
    public static bool CanOpen(string deviceType) => deviceType is "*SCS" or "*USERASCII";

    /// <summary>The PDF, or null when this spooled file's data can't be made into one.</summary>
    public static byte[]? From(string deviceType, byte[] data, string? title = null) => deviceType switch
    {
        "*SCS" => Pdf.Write(Scs.Render(data), title),
        "*USERASCII" when IsPdf(data) => data,
        _ => null,
    };

    public static bool IsPdf(ReadOnlySpan<byte> data) => data.StartsWith("%PDF-"u8);
}
