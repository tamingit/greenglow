using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

internal static class ReplyTestData
{
    public const byte BLANK = 0x40;

    public static Screen ScreenOf(params object[] orders)
    {
        var screen = new Screen();
        var data = B(CLEAR, Wtd(0x00, 0x08, orders), 0x04, 0x52, 0, 0);
        DataStream.ApplyRecord(screen, new HostRecord(0, Opcode.PutGet, data), true);
        return screen;
    }

    public static Screen TwoFields() => ScreenOf(Sba(2, 10), Sf(0x4000, 0x20, 5), Sba(3, 10), Sf(0x4000, 0x20, 4));

    public static Screen SignOn() => ScreenOf(Sba(6, 52), Sf(0x4020, 0x24, 10), Sba(7, 52), Sf(0x4020, 0x27, 10));

    public static int Aid(string name) => Keyboard.AidCodes[name];

    public static byte[] Blanks(int count) => Enumerable.Repeat(BLANK, count).ToArray();
}

public class ReadReplyTests
{
    private const byte BLANK = ReplyTestData.BLANK;
    private const int READ_INPUT_FIELDS = ReadCommand.ReadInputFields;
    private const int READ_MDT_FIELDS = ReadCommand.ReadMdtFields;
    private const int READ_MDT_ALTERNATE = ReadCommand.ReadMdtAlternate;
    private static readonly int Enter = ReplyTestData.Aid("Enter");

    [Fact(DisplayName = "READ replies: READ INPUT FIELDS sends every field whole, with nulls as blanks")]
    public void ReadInputFieldsSendsEveryField()
    {
        var screen = ReplyTestData.TwoFields();
        Type(screen, Typed("AB"));
        Assert.Equal(
            B(2, 13, 0xf1, Text("AB"), BLANK, BLANK, BLANK, BLANK, BLANK, BLANK, BLANK),
            Reply.EncodeReadReply(screen, READ_INPUT_FIELDS, Enter));
    }

    [Fact(DisplayName = "READ replies: sends only the cursor and AID when nothing is modified or the key sends no data")]
    public void SendsOnlyTheCursorAndAid()
    {
        var screen = ReplyTestData.TwoFields();
        Assert.Equal(B(2, 11, 0xf1), Reply.EncodeReadReply(screen, READ_INPUT_FIELDS, Enter));
        Type(screen, Typed("A"));
        Assert.Equal(B(2, 12, 0xf3), Reply.EncodeReadReply(screen, READ_MDT_FIELDS, ReplyTestData.Aid("Help")));
    }

    [Fact(DisplayName = "READ replies: READ MDT FIELDS strips trailing nulls and blanks the others; the alternate keeps them")]
    public void ReadMdtFieldsStripsTrailingNulls()
    {
        var screen = ReplyTestData.TwoFields();
        Type(screen, Typed("A"), Fn("Right"), Typed("B"));
        var fields = B(Sba(2, 11), Text("A"), BLANK, Text("B"));
        Assert.Equal(B(2, 14, 0xf1, fields), Reply.EncodeReadReply(screen, READ_MDT_FIELDS, Enter));
        Assert.Equal(
            B(2, 14, 0xf1, Sba(2, 11), Text("A"), 0x00, Text("B")),
            Reply.EncodeReadReply(screen, READ_MDT_ALTERNATE, Enter));
        // An all-null modified field sends just its address in the alternate form.
        Type(screen, At(3, 11), Fn("EraseEof"));
        Assert.Equal(
            B(3, 11, 0xf1, Sba(2, 11), Text("A"), 0x00, Text("B"), Sba(3, 11)),
            Reply.EncodeReadReply(screen, READ_MDT_ALTERNATE, Enter));
    }

    [Fact(DisplayName = "READ replies: never sends the sign position; a minus sets the zone of the last digit to D")]
    public void NeverSendsTheSignPosition()
    {
        var screen = ReplyTestData.ScreenOf(Sba(2, 10), Sf(0x4700, 0x20, 5), Sba(3, 10), Sf(0x4700, 0x20, 5));
        Type(screen, Typed("12"), Fn("FieldMinus"), Typed("34"), Fn("FieldPlus"));
        Assert.Equal(
            B(2, 11, 0xf1, BLANK, BLANK, 0xf1, 0xd2, BLANK, BLANK, 0xf3, 0xf4),
            Reply.EncodeReadReply(screen, READ_INPUT_FIELDS, Enter));
        Assert.Equal(
            B(2, 11, 0xf1, Sba(2, 11), BLANK, BLANK, 0xf1, 0xd2, Sba(3, 11), BLANK, BLANK, 0xf3, 0xf4),
            Reply.EncodeReadReply(screen, READ_MDT_FIELDS, Enter));
    }

    [Fact(DisplayName = "READ replies: leaves out field data for a PF key masked in the SOH header")]
    public void LeavesOutDataForAMaskedPfKey()
    {
        var screen = ReplyTestData.ScreenOf(new[] { 0x01, 0x07, 0, 0, 0, 24, 0, 0, 0x04 }, Sba(2, 10), Sf(0x4000, 0x20, 3));
        Type(screen, Typed("X"));
        Assert.Equal(B(2, 12, 0x33), Reply.EncodeReadReply(screen, READ_MDT_FIELDS, ReplyTestData.Aid("F3")));
        Assert.Equal(
            B(2, 12, 0x34, Sba(2, 11), Text("X")),
            Reply.EncodeReadReply(screen, READ_MDT_FIELDS, ReplyTestData.Aid("F4")));
    }

    [Fact(DisplayName = "READ replies: READ IMMEDIATE uses AID 0 and the input format; the modified alternate form sends MDT fields")]
    public void ReadImmediateUsesAid0()
    {
        var screen = ReplyTestData.TwoFields();
        Type(screen, Typed("Z"));
        Assert.Equal(
            B(2, 12, 0x00, Text("Z"), BLANK, BLANK, BLANK, BLANK, BLANK, BLANK, BLANK, BLANK),
            Reply.EncodeReadReply(screen, ReadCommand.ReadImmediate, 0));
        Assert.Equal(
            B(2, 12, 0x00, Sba(2, 11), Text("Z")),
            Reply.EncodeReadReply(screen, ReadCommand.ReadModifiedImmediateAlternate, 0));
    }
}

public class RecordedReplyCopyTests
{
    private static readonly int Enter = ReplyTestData.Aid("Enter");

    [Fact(DisplayName = "recorded copies of replies: blank nondisplay fields for their whole length")]
    public void BlankNondisplayFields()
    {
        var screen = ReplyTestData.SignOn();
        Type(screen, Typed("user1"), Fn("FieldExit"), Typed("SECRET9"));
        Assert.Equal(
            B(7, 60, 0xf1, Sba(6, 53), Text("USER1"), Sba(7, 53), ReplyTestData.Blanks(10)),
            Reply.EncodeReadReply(screen, ReadCommand.ReadMdtFields, Enter, Blank.Nondisplay));
    }

    [Fact(DisplayName = "recorded copies of replies: blank every input field with all-input")]
    public void BlankEveryInputField()
    {
        var screen = ReplyTestData.SignOn();
        Type(screen, Typed("user1"), Fn("FieldExit"), Typed("SECRET9"));
        var data = Reply.EncodeReadReply(screen, ReadCommand.ReadMdtFields, Enter, Blank.AllInput);
        Assert.Equal(ReplyTestData.Blanks(10), data[6..16]);
    }

    [Fact(DisplayName = "recorded copies of replies: blank a field whose attribute on screen was rewritten as nondisplay")]
    public void BlankARewrittenField()
    {
        var screen = ReplyTestData.SignOn();
        Type(screen, Typed("user1"));
        // The host writes a nondisplay attribute byte as plain data over the user field's attribute.
        DataStream.ApplyRecord(screen, new HostRecord(0, Opcode.PutGet, Wtd(0, 0, Sba(6, 52), 0x27)), true);
        var data = Reply.EncodeReadReply(screen, ReadCommand.ReadMdtFields, Enter, Blank.Nondisplay);
        Assert.Equal(ReplyTestData.Blanks(10), data[6..]);
    }
}
