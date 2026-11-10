using System.Text.RegularExpressions;
using GreenGlow.Engine;

namespace GreenGlow.Engine.Tests;

public class RecordTests
{
    private static byte[] FromHex(string hex) => Convert.FromHexString(Regex.Replace(hex, @"\s+", ""));

    /// <summary>Strips Telnet framing the way the decoder does.</summary>
    private static byte[] Unframe(byte[] bytes)
    {
        var events = new TelnetDecoder().Push(bytes);
        var only = Assert.Single(events);
        return Assert.IsType<TelnetEvent.Record>(only).Data;
    }

    private static HostRecord DataOf(ParsedRecord parsed) => Assert.IsType<ParsedRecord.Data>(parsed).Record;

    [Fact(DisplayName = "parseRecord: parses the RFC 1205 Turn On Message Light example")]
    public void ParsesTheMessageLightExample()
    {
        Assert.Equal(
            new ParsedRecord.Data(new HostRecord(0, Opcode.MessageLightOn, [])),
            Records.Parse(FromHex("000A 12A0 0000 0400 000B")));
    }

    [Fact(DisplayName = "parseRecord: returns the data after the header, and the flags")]
    public void ReturnsDataAndFlags()
    {
        Assert.Equal(
            new ParsedRecord.Data(new HostRecord(RecordFlag.Err, Opcode.PutGet, FromHex("044001"))),
            Records.Parse(FromHex("000D 12A0 0000 0480 0003 0440 01")));
    }

    [Fact(DisplayName = "parseRecord: parses the RFC 4777 Startup Response Record example")]
    public void ParsesTheStartupResponseExample()
    {
        var example = FromHex(
            "004912A090000560060020C0003D0000C9F9F0F2E3C1D9C7C5E34040D7C3D7D9"
            + "C9D5E3C5D9400000000000000000000000000000000000000000000000000000"
            + "000000000000000000");
        Assert.Equal(
            new ParsedRecord.Startup(new StartupResponse("I902", "TARGET", "PCPRINTER")),
            Records.Parse(example));
    }

    [Theory(DisplayName = "parseRecord: rejects")]
    [InlineData("000A12A0000004", "too short")]
    [InlineData("000B 12A0 0000 0400 000B", "length field says 11")]
    [InlineData("000A 12A1 0000 0400 000B", "not 12A0")]
    [InlineData("000A 12A0 0000 0900 000B", "variable header length 9")]
    public void Rejects(string hex, string reason)
    {
        var parsed = Assert.IsType<ParsedRecord.Invalid>(Records.Parse(FromHex(hex)));
        Assert.Contains(reason, parsed.Reason);
    }

    [Fact(DisplayName = "encodeClientRecord: builds the RFC 1205 example and frames it with IAC EOR")]
    public void BuildsTheRfcExample()
    {
        Assert.Equal(FromHex("000A 12A0 0000 0400 000B FFEF"), Records.EncodeClientRecord(Opcode.MessageLightOn, 0, []));
    }

    [Fact(DisplayName = "encodeClientRecord: computes the length before doubling 0xFF")]
    public void ComputesTheLengthBeforeDoubling()
    {
        var framed = Records.EncodeClientRecord(Opcode.PutGet, 0, FromHex("FF01"));
        Assert.Equal(FromHex("000C 12A0 0000 0400 0003 FFFF 01 FFEF"), framed);
        Assert.Equal(FromHex("FF01"), DataOf(Records.Parse(Unframe(framed))).Data);
    }

    [Fact(DisplayName = "describeStartup: marks I902 as success and 8902 and 8940 as failures with a reason")]
    public void DescribesStartupCodes()
    {
        StartupResult Describe(string code) => StartupCodes.Describe(new StartupResponse(code, "SYS", "DEV"));
        Assert.True(Describe("I902").Ok);
        var inUse = Describe("8902");
        Assert.False(inUse.Ok);
        Assert.Contains("in use", inUse.Message);
        var create = Describe("8940");
        Assert.False(create.Ok);
        Assert.Contains("create a new device", create.Message);
        var unknown = Describe("ZZZZ");
        Assert.False(unknown.Ok);
        Assert.Contains("Unknown", unknown.Message);
    }

    [Fact(DisplayName = "EBCDIC CCSID 37: maps the characters that differ between EBCDIC code pages")]
    public void MapsTheCharactersThatDiffer()
    {
        Assert.Equal(" ¢.|!$¬@^[]\\Aa0", Ebcdic.Decode(FromHex("40 4A 4B 4F 5A 5B 5F 7C B0 BA BB E0 C1 81 F0")));
    }

    [Fact(DisplayName = "EBCDIC CCSID 37: round-trips every byte and substitutes what it cannot encode")]
    public void RoundTripsEveryByte()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Assert.Equal(all, Ebcdic.Encode(Ebcdic.Decode(all)));
        Assert.Equal([Ebcdic.Substitute], Ebcdic.Encode("€"));
    }

    [Fact(DisplayName = "5250 QUERY: recognises the QUERY command")]
    public void RecognisesTheQuery()
    {
        Assert.True(Query.IsQuery(FromHex("04F3 0005 D970 00")));
        Assert.False(Query.IsQuery(FromHex("04F3 0005 D971 00")));
        Assert.False(Query.IsQuery(FromHex("0440")));
    }

    [Fact(DisplayName = "5250 QUERY: replies as a 27 x 132 colour 3477-FC with no enhanced features")]
    public void RepliesAsA3477()
    {
        var record = DataOf(Records.Parse(Unframe(Query.EncodeQueryReply(Query.Terminals["IBM-3477-FC"]))));
        var data = record.Data;
        Assert.Equal(Opcode.NoOperation, record.Opcode);
        Assert.Equal(3 + 0x44, data.Length);
        // Cursor 0,0, AID X'88', structured field X'0044' D9 70 80.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x88, 0x00, 0x44, 0xd9, 0x70, 0x80 }, data[..8]);
        // Display station 3477, model "FC ".
        Assert.Equal(0x01, data[29]);
        Assert.Equal("3477FC ", Ebcdic.Decode(data.AsSpan(30, 7)));
        // Device capabilities: byte 1 has 27 x 132, 24 x 80 and colour; byte 4 (enhanced) is off.
        var capabilities = data[49..];
        Assert.Equal(22, capabilities.Length);
        Assert.Equal(0x23, capabilities[0]);
        Assert.Equal(0x31, capabilities[1]);
        Assert.Equal(0x00, capabilities[4]);
    }

    [Fact(DisplayName = "5250 QUERY: leaves the 27 x 132 bit off for a 24 x 80 terminal and zero-pads a numeric model")]
    public void LeavesTheWideBitOff()
    {
        var record = DataOf(Records.Parse(Unframe(Query.EncodeQueryReply(Query.Terminals["IBM-3179-2"]))));
        Assert.Equal("3179002", Ebcdic.Decode(record.Data.AsSpan(30, 7)));
        Assert.Equal(0x11, record.Data[49 + 1]);
    }

    [Fact(DisplayName = "negative responses: carry the ERR flag, No Operation and the 4-byte code (RFC 1205, table 13-26)")]
    public void NegativeResponsesCarryTheCode()
    {
        Assert.Equal(FromHex("000E 12A0 0000 0480 0000 1005 0142 FFEF"), NegativeResponse.Encode(NegativeResponse.DataNotValid));
        Assert.Equal(FromHex("000E 12A0 0000 0480 0000 1003 0101 FFEF"), NegativeResponse.Encode(NegativeResponse.CommandNotValid));
    }

    [Fact(DisplayName = "negative responses: print the code as the manual writes it")]
    public void PrintTheCode()
    {
        Assert.Equal("1005 012B", NegativeResponse.FormatResponseCode(0x1005012b));
        Assert.Equal("1003 0101", NegativeResponse.FormatResponseCode(0x10030101));
    }
}
