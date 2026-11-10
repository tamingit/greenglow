using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

// Synthetic data streams shaped like a sign-on screen. Placeholder text only.
internal static class ScreenTestData
{
    public static HostRecord PutRecord(params object[] data) => new(0, Opcode.PutGet, B(data));

    public static (Screen Screen, ApplyResult Result, ScreenSnapshot Snapshot) Apply(
        HostRecord record,
        Screen? screen = null,
        bool wide = true)
    {
        screen ??= new Screen();
        var result = DataStream.ApplyRecord(screen, record, wide);
        return (screen, result, screen.Snapshot());
    }

    public static string Row(ScreenSnapshot snapshot, int n) => Display.ScreenText(snapshot)[n - 1];
}

public class WriteToDisplayTests
{
    private static HostRecord SignOn() =>
        ScreenTestData.PutRecord(
            CLEAR,
            Wtd(
                0x00,
                0x18,
                new[] { 0x01, 0x07, 0x00, 0x00, 0x00, 0x19, 0x00, 0x00, 0x00 }, // SOH, error row 25
                Sba(1, 10),
                0x22,
                Text("Sign On"),
                new[] { 0x02, 1, 30, 0x40 },
                0x20, // RA spaces to column 30
                Sba(3, 5),
                0x20,
                Text("User"),
                Sba(3, 20),
                new[] { 0x1d, 0x40, 0x20, 0x24, 0x00, 0x05 },
                new[] { 0, 0, 0, 0, 0 }, // monocase input
                Sba(4, 5),
                0x20,
                Text("Password"),
                Sba(4, 20),
                new[] { 0x1d, 0x40, 0x20, 0x27, 0x00, 0x05 }, // nondisplay input
                Sba(5, 20),
                new[] { 0x1d, 0x60, 0x00, 0x27, 0x00, 0x05 }, // bypass, hidden
                Sba(6, 1),
                new[] { 0x02, 6, 5, 0xc1 }), // RA of "A"
            new[] { ESC, 0x52, 0x00, 0x00 }); // READ MDT FIELDS

    private static (Screen Screen, ApplyResult Result, ScreenSnapshot Snapshot) Apply(HostRecord record, Screen? screen = null, bool wide = true) =>
        ScreenTestData.Apply(record, screen, wide);

    private static HostRecord P(params object[] data) => ScreenTestData.PutRecord(data);

    private static string Row(ScreenSnapshot snapshot, int n) => ScreenTestData.Row(snapshot, n);

    [Fact(DisplayName = "WRITE TO DISPLAY: draws text, attributes and repeated characters")]
    public void DrawsTextAttributesAndRepeats()
    {
        var (_, result, snapshot) = Apply(SignOn());
        Assert.Empty(result.Events);
        Assert.True(result.Changed);
        Assert.Equal((24, 80), (snapshot.Rows, snapshot.Columns));
        Assert.Equal("Sign On", Row(snapshot, 1)[10..17]);
        Assert.Equal("User", Row(snapshot, 3)[5..9]);
        Assert.Equal("AAAAA", Row(snapshot, 6)[0..5]);
        // Attribute positions show as blanks; the attribute applies to the text after it.
        Assert.Equal(0x22, snapshot.Buffer[9]);
        Assert.Equal(0x22, snapshot.Attributes[10]);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: defines input fields with their format, leading and ending attributes")]
    public void DefinesInputFields()
    {
        var (_, _, snapshot) = Apply(SignOn());
        Assert.Equal(
            [(3, 21, 5, 0x24), (4, 21, 5, 0x27), (5, 21, 5, 0x27)],
            snapshot.Fields.Select(f => (f.Row, f.Column, f.Length, f.Attribute)).ToList());
        var format = snapshot.Fields[0].Format;
        Assert.True(format.Monocase);
        Assert.False(format.Bypass);
        Assert.Equal("alpha-shift", format.Shift);
        Assert.True(snapshot.Fields[2].Format.Bypass);
        // Leading attribute at the SF address, ending attribute after the last position.
        Assert.Equal(0x24, snapshot.Buffer[2 * 80 + 19]);
        Assert.Equal(0x20, snapshot.Buffer[2 * 80 + 25]);
        Assert.Equal("", Display.FieldText(snapshot, snapshot.Fields[0]));
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: unlocks the keyboard, puts the cursor in the first input field and notes the pending read")]
    public void UnlocksAndNotesTheRead()
    {
        var (_, _, snapshot) = Apply(SignOn());
        Assert.False(snapshot.KeyboardLocked);
        Assert.Equal(new Position(3, 21), snapshot.Cursor);
        Assert.Equal(25, snapshot.ErrorRow);
        Assert.Equal(0x52, snapshot.PendingRead);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: hides text under a nondisplay attribute")]
    public void HidesNondisplayText()
    {
        var screen = Apply(SignOn()).Screen;
        var (_, _, snapshot) = Apply(P(Wtd(0x00, 0x00, Sba(4, 21), Text("HIDE"))), screen);
        Assert.Equal("     ", Row(snapshot, 4)[20..25]);
        Assert.Equal("HIDE", Display.FieldText(snapshot, snapshot.Fields[1]));
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: uses the IC address, and a later MC wins over it")]
    public void UsesTheIcAddress()
    {
        Assert.Equal(new Position(2, 2), Apply(P(CLEAR, Wtd(0, 0x08, new[] { 0x13, 2, 2 }))).Snapshot.Cursor);
        Assert.Equal(
            new Position(9, 9),
            Apply(P(CLEAR, Wtd(0, 0x08, new[] { 0x13, 2, 2 }, new[] { 0x14, 9, 9 }))).Snapshot.Cursor);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: leaves the keyboard locked without the unlock bit, and handles alarm and message light")]
    public void HandlesAlarmAndMessageLight()
    {
        var (_, result, snapshot) = Apply(P(CLEAR, Wtd(0, 0x04 | 0x01, Text("X"))));
        Assert.True(snapshot.KeyboardLocked);
        Assert.True(snapshot.MessageWaiting);
        Assert.Equal([new ConnectionEvent.Alarm()], result.Events);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: resets MDT flags and nulls fields as control byte 0 says")]
    public void ResetsMdtFlags()
    {
        var screen = Apply(P(CLEAR, Wtd(0, 0x08, Sba(2, 1), new[] { 0x1d, 0x48, 0x00, 0x20, 0x00, 0x03 }, Text("ABC")))).Screen;
        Assert.True(screen.Snapshot().Fields[0].Format.Modified);
        var (_, _, snapshot) = Apply(P(Wtd(0xe0, 0x00)), screen); // reset all MDT, null non-bypass
        Assert.False(snapshot.Fields[0].Format.Modified);
        Assert.Equal("", Display.FieldText(snapshot, snapshot.Fields[0]));
        Assert.True(snapshot.KeyboardLocked);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: modifies an existing field instead of adding a second one")]
    public void ModifiesAnExistingField()
    {
        var screen = Apply(P(CLEAR, Wtd(0, 0, Sba(2, 1), new[] { 0x1d, 0x40, 0x00, 0x20, 0x00, 0x03 }))).Screen;
        var (_, _, snapshot) = Apply(P(Wtd(0, 0, Sba(2, 1), new[] { 0x1d, 0x60, 0x00, 0x22, 0x00, 0x03 })), screen);
        var field = Assert.Single(snapshot.Fields);
        Assert.Equal(0x22, field.Attribute);
        Assert.True(field.Format.Bypass);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: allows row 1, column 0 before an SF so the field starts at row 1, column 1")]
    public void AllowsRow1Column0BeforeAnSf()
    {
        var (_, result, snapshot) = Apply(P(CLEAR, Wtd(0, 0, Sba(1, 0), new[] { 0x1d, 0x40, 0x00, 0x20, 0x00, 0x04 })));
        Assert.Empty(result.Events);
        Assert.Equal((1, 1, 4), (snapshot.Fields[0].Row, snapshot.Fields[0].Column, snapshot.Fields[0].Length));
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: keeps field control words and output-only fields")]
    public void KeepsFieldControlWords()
    {
        var (_, _, snapshot) = Apply(
            P(
                CLEAR,
                Wtd(
                    0,
                    0,
                    Sba(2, 1),
                    new[] { 0x1d, 0x40, 0x00, 0x86, 0x01, 0x20, 0x00, 0x04 },
                    Sba(3, 1),
                    new[] { 0x1d, 0x22, 0x00, 0x04 },
                    Text("OUT"))));
        var field = Assert.Single(snapshot.Fields);
        Assert.Equal([0x8601], field.Fcws);
        Assert.Equal("OUT", Row(snapshot, 3)[1..4]);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: erases to an address and writes transparent data")]
    public void ErasesToAnAddressAndWritesTransparentData()
    {
        var screen = Apply(P(CLEAR, Wtd(0, 0, Sba(2, 1), Text("ABCDEF")))).Screen;
        var (_, _, snapshot) = Apply(
            P(Wtd(0, 0, Sba(2, 2), new[] { 0x03, 2, 3, 0x02, 0xff }, new[] { 0x10, 0x00, 0x02 }, Text("XY"))),
            screen);
        Assert.Equal("A  XYF", Row(snapshot, 2)[0..6]);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: switches to 27 x 132 with CLEAR UNIT ALTERNATE, but only on a wide terminal")]
    public void SwitchesTo27x132()
    {
        var wide = Apply(P(new[] { ESC, 0x20, 0x00 })).Snapshot;
        Assert.Equal((27, 132, 27), (wide.Rows, wide.Columns, wide.ErrorRow));
        var narrow = Apply(P(new[] { ESC, 0x20, 0x00 }), new Screen(), false);
        Assert.Equal("data-stream-error", narrow.Result.Events[0].Type);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: writes bytes below 0x20 that are not orders as data, shown as a block")]
    public void WritesLowBytesAsData()
    {
        // A source line in SEU can hold one, as in "adapter <1F> freight".
        var (_, result, snapshot) = Apply(
            P(CLEAR, Wtd(0, 0, Sba(2, 1), Text("A"), 0x1f, Text("B"), 0x05, 0x1e, 0x00), new[] { ESC, 0x52, 0, 0 }));
        Assert.Empty(result.Events);
        Assert.Equal("A█B█  ", Row(snapshot, 2)[0..6]);
        Assert.Equal(0x52, snapshot.PendingRead);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: reports bad orders and addresses as data stream errors")]
    public void ReportsBadOrders()
    {
        Assert.Equal(
            [new ConnectionEvent.DataStreamError("0xff is not a valid data character")],
            Apply(P(CLEAR, Wtd(0, 0, 0xff))).Result.Events);
        Assert.Equal(
            "row 25, column 1 is off the screen",
            Assert.IsType<ConnectionEvent.DataStreamError>(Apply(P(CLEAR, Wtd(0, 0, Sba(25, 1)))).Result.Events[0]).Message);
        Assert.Equal(
            "repeat to address is before the current address",
            Assert.IsType<ConnectionEvent.DataStreamError>(
                Apply(P(CLEAR, Wtd(0, 0, Sba(2, 10), new[] { 0x02, 2, 5, 0x40 }))).Result.Events[0]).Message);
        Assert.Equal(
            "expected ESC but found 0x11",
            Assert.IsType<ConnectionEvent.DataStreamError>(Apply(P(new[] { 0x11 })).Result.Events[0]).Message);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: reports commands it does not handle yet")]
    public void ReportsUnhandledCommands()
    {
        // READ SCREEN WITH EXTENDED ATTRIBUTES: not declared in the QUERY reply.
        Assert.Equal([new ConnectionEvent.Unsupported("command 0x64")], Apply(P(CLEAR, new[] { ESC, 0x64 })).Result.Events);
        Assert.Empty(Apply(new HostRecord(0, Opcode.SaveScreen, [])).Result.Events);
    }

    [Fact(DisplayName = "WRITE TO DISPLAY: skips the 5250 QUERY structured field")]
    public void SkipsTheQuery()
    {
        var (_, result, _) = Apply(P(new[] { ESC, 0xf3, 0x00, 0x05, 0xd9, 0x70, 0x00 }));
        Assert.Empty(result.Events);
    }
}

public class ScreenNegativeResponseTests
{
    private static readonly byte[] READ_MDT_ = [ESC, 0x52, 0x00, 0x00];

    /// <summary>SOH that puts the error row on the separate message line (row 25 of 24).</summary>
    private static readonly byte[] SOH_ROW_25 = B(0x01, 0x07, 0x00, 0x00, 0x00, 0x19, 0x00, 0x00, 0x00);

    private static HostRequest Nr(int code) => new HostRequest.NegativeResponse(code);

    private static HostRecord P(params object[] data) => ScreenTestData.PutRecord(data);

    // Each failure and the table 13-26 code it gets.
    public static TheoryData<string, HostRecord, int, bool> Cases => new()
    {
        { "no ESC where a command should start", P(new[] { 0x11 }), 0x10050131, true },
        { "27 x 132 on a 24 x 80 terminal", P(new[] { ESC, 0x20, 0x00 }), 0x10030101, false },
        { "a CLEAR UNIT ALTERNATE parameter that is not valid", P(new[] { ESC, 0x20, 0x40 }), 0x10030105, true },
        { "READ SCREEN that is not last", P(new[] { ESC, 0x62 }, CLEAR), 0x10030101, true },
        { "READ IMMEDIATE that is not last", P(new[] { ESC, 0x72 }, CLEAR), 0x10030101, true },
        { "a RESTORE PARTIAL SCREEN length below 2", P(new[] { ESC, 0x13, 0x00, 0x01 }), 0x10050127, true },
        { "saved data that cannot be decoded", P(new[] { ESC, 0x12, 1, 2, 3 }), 0x10050127, true },
        {
            "WRITE ERROR CODE TO WINDOW on the separate message line",
            P(CLEAR, Wtd(0, 0, SOH_ROW_25), new[] { ESC, 0x22, 1, 10 }),
            0x10050133,
            true
        },
        { "error window columns that are not valid", P(CLEAR, new[] { ESC, 0x22, 10, 5 }), 0x10050132, true },
        { "ROLL parameters that are not valid", P(CLEAR, new[] { ESC, 0x23, 0x01, 0, 24 }), 0x1005012c, true },
        { "RA before the current address", P(CLEAR, Wtd(0, 0, Sba(2, 10), new[] { 0x02, 2, 5, 0x40 })), 0x10050123, true },
        { "EA before the current address", P(CLEAR, Wtd(0, 0, Sba(2, 10), new[] { 0x03, 2, 5, 2, 0 })), 0x10050123, true },
        { "an EA length that is not valid", P(CLEAR, Wtd(0, 0, Sba(2, 10), new[] { 0x03, 2, 20, 6 })), 0x1005012d, true },
        { "an SOH length that is not valid", P(CLEAR, Wtd(0, 0, new[] { 0x01, 0x00 })), 0x1005012b, true },
        { "X'FF' as data", P(CLEAR, Wtd(0, 0, 0xff)), 0x10050142, true },
        {
            "an SF attribute that is not valid",
            P(CLEAR, Wtd(0, 0, Sba(2, 1), new[] { 0x1d, 0x40, 0x00, 0x41, 0, 5 })),
            0x10050130,
            true
        },
        { "an SF length of 0", P(CLEAR, Wtd(0, 0, Sba(2, 1), new[] { 0x1d, 0x40, 0x00, 0x20, 0, 0 })), 0x10050125, true },
        {
            "a field that runs off the screen",
            P(CLEAR, Wtd(0, 0, Sba(24, 70), new[] { 0x1d, 0x40, 0x00, 0x20, 0, 20 })),
            0x10050128,
            true
        },
        { "an SBA off the screen", P(CLEAR, Wtd(0, 0, Sba(25, 1))), 0x10050122, true },
        { "row 1, column 0 without an SF", P(CLEAR, Wtd(0, 0, Sba(1, 0), Text("A"))), 0x10050122, true },
        { "an IC off the screen", P(CLEAR, Wtd(0, 0, new[] { 0x13, 1, 81 })), 0x10050122, true },
        { "an MC off the screen", P(CLEAR, Wtd(0, 0, new[] { 0x14, 0, 1 })), 0x10050122, true },
        { "a record that ends inside an order", P(CLEAR, Wtd(0, 0, new[] { 0x11, 2 })), 0x10050121, true },
        { "a record that ends inside a command", P(new[] { ESC, 0x11, 0x00 }), 0x10050121, true },
        { "an unknown command", P(CLEAR, new[] { ESC, 0x99 }), 0x10030101, true },
        { "READ SCREEN WITH EXTENDED ATTRIBUTES", P(CLEAR, new[] { ESC, 0x64 }), 0x10030101, true },
        { "READ SCREEN TO PRINT", P(CLEAR, new[] { ESC, 0x66 }), 0x10030101, true },
        { "QUERY STATION STATE", P(new[] { ESC, 0xf3, 0x00, 0x06, 0xd9, 0x72, 0x00, 0x00 }), 0x10050111, true },
    };

    [Theory(DisplayName = "negative responses: rejects")]
    [MemberData(nameof(Cases))]
    public void Rejects(string name, HostRecord record, int code, bool wide)
    {
        _ = name;
        var (_, result, _) = ScreenTestData.Apply(record, new Screen(), wide);
        var only = Assert.Single(result.Events);
        Assert.Contains(only.Type, new[] { "data-stream-error", "unsupported" });
        Assert.Equal(Nr(code), result.Requests[^1]);
    }

    [Fact(DisplayName = "negative responses: keeps what came before the error and skips the rest, the unlock and the READ")]
    public void KeepsWhatCameBefore()
    {
        // Control byte 1: unlock, alarm and message light on. None of them may apply.
        var (_, result, snapshot) = ScreenTestData.Apply(
            P(CLEAR, Wtd(0x20, 0x08 | 0x04 | 0x01, Sba(2, 1), Text("Before"), 0xff, Text("After")), READ_MDT_));
        Assert.Equal([Nr(0x10050142)], result.Requests);
        Assert.Equal("Before      ", ScreenTestData.Row(snapshot, 2)[0..12]);
        Assert.Equal(KeyboardState.Locked, snapshot.Keyboard);
        Assert.Null(snapshot.PendingRead);
        Assert.False(snapshot.MessageWaiting);
        Assert.DoesNotContain(new ConnectionEvent.Alarm(), result.Events);
    }

    [Fact(DisplayName = "negative responses: leaves the keyboard as the bytes before the error left it")]
    public void LeavesTheKeyboardAsItWas()
    {
        var first = Wtd(0x00, 0x08, Sba(2, 1), new[] { 0x1d, 0x40, 0x00, 0x20, 0, 5 });
        // Control byte 0 of 000 leaves the keyboard alone; any other mode locks it.
        var unlocked = ScreenTestData.Apply(P(CLEAR, first, Wtd(0x00, 0x00, Sba(5, 1), 0xff), READ_MDT_));
        Assert.Equal(KeyboardState.Unlocked, unlocked.Snapshot.Keyboard);
        Assert.Null(unlocked.Snapshot.PendingRead);
        var locked = ScreenTestData.Apply(P(CLEAR, first, Wtd(0x20, 0x00, Sba(5, 1), 0xff), READ_MDT_));
        Assert.Equal(KeyboardState.Locked, locked.Snapshot.Keyboard);
    }

    [Fact(DisplayName = "negative responses: keeps a READ that an earlier record left pending")]
    public void KeepsAnEarlierRead()
    {
        var (screen, _, _) = ScreenTestData.Apply(
            P(CLEAR, Wtd(0x00, 0x08, Sba(2, 1), new[] { 0x1d, 0x40, 0x00, 0x20, 0, 5 }), READ_MDT_));
        var (_, result, snapshot) = ScreenTestData.Apply(P(Wtd(0x00, 0x00, Sba(5, 1), 0xff)), screen);
        Assert.Equal([Nr(0x10050142)], result.Requests);
        Assert.Equal(0x52, snapshot.PendingRead);
        Assert.Equal(KeyboardState.Unlocked, snapshot.Keyboard);
    }

    [Fact(DisplayName = "negative responses: answers what came earlier in the record first")]
    public void AnswersWhatCameEarlierFirst()
    {
        var query = B(ESC, 0xf3, 0x00, 0x05, 0xd9, 0x70, 0x00);
        var (_, result, _) = ScreenTestData.Apply(P(query, CLEAR, Wtd(0, 0, 0xff)));
        Assert.Equal([new HostRequest.Query(), Nr(0x10050142)], result.Requests);
    }

    [Fact(DisplayName = "negative responses: skips a WDSF order without a negative response, so the screen still unlocks")]
    public void SkipsAWdsfOrder()
    {
        var wdsf = B(0x15, 0x00, 0x06, 0xd9, 0x51, 0x00, 0x00);
        var (_, result, snapshot) = ScreenTestData.Apply(P(CLEAR, Wtd(0x00, 0x08, wdsf, Sba(2, 1), Text("Text")), READ_MDT_));
        Assert.Equal([new ConnectionEvent.Unsupported("WDSF structured field 0xd9 0x51")], result.Events);
        Assert.Empty(result.Requests);
        Assert.Equal("Text", ScreenTestData.Row(snapshot, 2)[0..4]);
        Assert.Equal(KeyboardState.Unlocked, snapshot.Keyboard);
        Assert.Equal(0x52, snapshot.PendingRead);
    }
}

public class DecodeFieldFormatTests
{
    [Fact(DisplayName = "decodeFieldFormat: decodes every flag")]
    public void DecodesEveryFlag()
    {
        Assert.Equal(
            new FieldFormat(
                Bypass: true,
                DupEnable: true,
                Modified: true,
                Shift: "signed-numeric",
                AutoEnter: true,
                FieldExitRequired: true,
                Monocase: true,
                MandatoryEnter: true,
                Adjust: "mandatory-fill"),
            FieldFormat.Decode(0x7fff & ~0x0010));
        var format = FieldFormat.Decode(0x4305);
        Assert.Equal("numeric-only", format.Shift);
        Assert.Equal("right-zero", format.Adjust);
    }
}
