using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

// Synthetic screens only: fields are described by row, column, length and format word.
internal sealed record FieldSpec(int Row, int Column, int Length, int Ffw, int Attribute = 0x24);

internal static class KeyboardTestData
{
    public const int ALPHA = 0x4000;
    public const int MONOCASE = 0x4020;

    public static Screen Apply(Screen screen, params object[] data)
    {
        var result = DataStream.ApplyRecord(screen, new HostRecord(0, Opcode.PutGet, B(data)), true);
        Assert.DoesNotContain(result.Events, e => e.Type != "alarm");
        return screen;
    }

    /// <summary>A 24 x 80 screen with these input fields, keyboard unlocked, READ MDT FIELDS pending.</summary>
    public static Screen ScreenWith(FieldSpec[] fields, int[]? header = null, bool read = true)
    {
        var orders = new List<byte>();
        if (header is not null) orders.AddRange(B(0x01, header.Length, header));
        foreach (var f in fields)
        {
            orders.AddRange(B(0x11, f.Row, f.Column - 1));
            orders.AddRange(B(0x1d, f.Ffw >> 8, f.Ffw & 0xff, f.Attribute, 0, f.Length));
        }
        return Apply(new Screen(), B(ESC, 0x40), B(ESC, 0x11, 0x00, 0x08, orders), read ? READ_MDT : []);
    }

    /// <summary>Strings are typed one character key per character.</summary>
    public static KeyResult Press(Screen screen, params object[] keys)
    {
        var total = new KeyResult();
        foreach (var key in keys)
        {
            var list = key is string text ? Typed(text) : [(Key)key];
            foreach (var k in list)
            {
                var result = Keyboard.HandleKey(screen, k);
                total.Changed |= result.Changed;
                total.Alarm |= result.Alarm;
                total.Actions.AddRange(result.Actions);
            }
        }
        return total;
    }

    public static int Index(int row, int column) => (row - 1) * 80 + column - 1;

    public static string Text(Screen screen, int row, int column, int length) =>
        Ebcdic.Decode(screen.Buffer.AsSpan(Index(row, column), length));

    public static byte[] Raw(Screen screen, int row, int column, int length) =>
        screen.Buffer.AsSpan(Index(row, column), length).ToArray();

    public static Position Cursor(Screen screen) => new(screen.Cursor / 80 + 1, screen.Cursor % 80 + 1);

    public static int? ErrorCode(Screen screen) => screen.Error?.Code;

    public static int AidCode(string name) => Keyboard.AidCodes[name];

    public static KeyAction Aid(int code) => new KeyAction.Aid(code);
}

public class TypingTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "typing: writes characters, sets the MDT and moves to the next field after the last position")]
    public void WritesCharacters()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA), new(4, 21, 3, ALPHA)]);
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
        Assert.True(KeyboardTestData.Press(screen, "abc").Changed);
        Assert.Equal("abc", KeyboardTestData.Text(screen, 3, 21, 3));
        Assert.Equal(0x0800, screen.Fields[0].Ffw & 0x0800);
        Assert.Equal(new Position(4, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "typing: uppercases letters in a monocase field")]
    public void UppercasesInMonocase()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 5, KeyboardTestData.MONOCASE)]);
        KeyboardTestData.Press(screen, "qsys1");
        Assert.Equal("QSYS1", KeyboardTestData.Text(screen, 3, 21, 5));
    }

    [Fact(DisplayName = "typing: posts 0005 outside an input field and in a bypass field")]
    public void Posts0005()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA), new(5, 21, 3, 0x6000)]);
        KeyboardTestData.Press(screen, At(1, 1));
        var result = KeyboardTestData.Press(screen, "x");
        Assert.True(result.Alarm);
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
        Assert.Equal(OperatorError.CursorProtected, KeyboardTestData.ErrorCode(screen));
        Assert.Equal(new Position(1, 1), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), At(5, 21), "z");
        Assert.Equal(OperatorError.CursorProtected, KeyboardTestData.ErrorCode(screen)); // bypass field
    }

    [Fact(DisplayName = "typing: enforces the shift of each field type")]
    public void EnforcesTheShift()
    {
        (int Ffw, string Good, string Bad, int Code)[] cases =
        [
            (0x4100, "Ab,.- ", "1", OperatorError.AlphaOnly),
            (0x4300, "12+,.- ", "A", OperatorError.NumericOnly),
            (0x4500, "0123456789", "-", OperatorError.DigitsOnly),
            (0x4700, "12", "A", OperatorError.DigitsOnly),
            (0x4600, "", "A", OperatorError.DataNotAllowed),
        ];
        foreach (var (ffw, good, bad, code) in cases)
        {
            var screen = KeyboardTestData.ScreenWith([new(3, 21, 12, ffw)]);
            KeyboardTestData.Press(screen, good);
            Assert.Null(screen.Error);
            KeyboardTestData.Press(screen, bad);
            Assert.Equal(code, KeyboardTestData.ErrorCode(screen));
        }
    }

    [Fact(DisplayName = "typing: ignores characters CCSID 37 cannot encode")]
    public void IgnoresUnencodableCharacters()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        var result = KeyboardTestData.Press(screen, "€");
        Assert.True(result.Alarm);
        Assert.False(result.Changed);
        Assert.Null(screen.Error);
        Assert.Equal(B(0), KeyboardTestData.Raw(screen, 3, 21, 1));
    }

    [Fact(DisplayName = "typing: inserts by shifting right, and posts 0012 when the field is full")]
    public void InsertsByShiftingRight()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 4, ALPHA)]);
        KeyboardTestData.Press(screen, "AC", Fn("Left"), Fn("Insert"), "B");
        Assert.Equal("BC\0", KeyboardTestData.Text(screen, 3, 22, 3));
        Assert.True(screen.InsertMode);
        KeyboardTestData.Press(screen, "X");
        Assert.Equal("ABXC", KeyboardTestData.Text(screen, 3, 21, 4));
        KeyboardTestData.Press(screen, At(3, 21), "Y");
        Assert.Equal(OperatorError.NoRoom, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), Fn("Reset"));
        Assert.False(screen.InsertMode);
    }
}

public class FieldExitKeyTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "field exit keys: Field Exit nulls the rest of the field and moves to the next field")]
    public void FieldExitNullsTheRest()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 5, ALPHA), new(4, 21, 5, ALPHA)]);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x00, 0x11, 3, 21, Ebcdic.Encode("HELLO")));
        KeyboardTestData.Press(screen, At(3, 23), Fn("FieldExit"));
        Assert.Equal(B(Ebcdic.Encode("HE"), 0, 0, 0), KeyboardTestData.Raw(screen, 3, 21, 5));
        Assert.Equal(new Position(4, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "field exit keys: right-adjusts with zero or blank fill")]
    public void RightAdjusts()
    {
        var screen = KeyboardTestData.ScreenWith(
        [
            new(3, 21, 5, 0x4305), // numeric only, right adjust zero fill
            new(4, 21, 5, 0x4006), // right adjust blank fill
        ]);
        KeyboardTestData.Press(screen, "42", Fn("FieldExit"), "AB", Fn("FieldExit"));
        Assert.Equal("00042", KeyboardTestData.Text(screen, 3, 21, 5));
        Assert.Equal("   AB", KeyboardTestData.Text(screen, 4, 21, 5));
    }

    [Fact(DisplayName = "field exit keys: handles signed numeric fields: no typing in the sign position, Field− and Field+")]
    public void HandlesSignedNumericFields()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 5, 0x4700), new(4, 21, 5, 0x4700)]);
        KeyboardTestData.Press(screen, "1234");
        Assert.Equal(new Position(3, 25), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, "5");
        Assert.Equal(OperatorError.SignPosition, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), At(3, 21), "12", Fn("FieldMinus"));
        Assert.Equal("  12-", KeyboardTestData.Text(screen, 3, 21, 5));
        Assert.Equal(new Position(4, 21), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, "7", Fn("FieldPlus"));
        Assert.Equal("   7 ", KeyboardTestData.Text(screen, 4, 21, 5));
    }

    [Fact(DisplayName = "field exit keys: Field− is only for numeric fields, and zones the low-order digit of a numeric-only field")]
    public void FieldMinusIsOnlyForNumericFields()
    {
        var alpha = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        KeyboardTestData.Press(alpha, "A", Fn("FieldMinus"));
        Assert.Equal(OperatorError.FieldMinus, KeyboardTestData.ErrorCode(alpha));

        var numeric = KeyboardTestData.ScreenWith([new(3, 21, 4, 0x4300)]);
        KeyboardTestData.Press(numeric, "15", Fn("FieldMinus"));
        Assert.Equal(B(0xf1, 0xd5, 0, 0), KeyboardTestData.Raw(numeric, 3, 21, 4));

        var symbol = KeyboardTestData.ScreenWith([new(3, 21, 4, 0x4300)]);
        KeyboardTestData.Press(symbol, "1.", Fn("FieldMinus"));
        Assert.Equal(OperatorError.LowOrderNotDigit, KeyboardTestData.ErrorCode(symbol));
        Assert.Equal("1.", KeyboardTestData.Text(symbol, 3, 21, 2));
    }

    [Fact(DisplayName = "field exit keys: holds the cursor on the last position of a field exit required field")]
    public void HoldsTheCursorOnTheLastPosition()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 1, 0x4040), new(4, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, "1");
        Assert.True(screen.FieldExitPending);
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, "2");
        Assert.Equal(OperatorError.ExitKeyRequired, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"));
        // Reset clears the error but the field still needs a field exit key.
        Assert.True(screen.FieldExitPending);
        KeyboardTestData.Press(screen, Fn("FieldExit"));
        Assert.Equal("1", KeyboardTestData.Text(screen, 3, 21, 1));
        Assert.Equal(new Position(4, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "field exit keys: lets Left clear the field exit wait so the last character can be retyped")]
    public void LeftClearsTheFieldExitWait()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 2, 0x4040)]);
        KeyboardTestData.Press(screen, "12", Fn("Left"), "3");
        Assert.Equal("13", KeyboardTestData.Text(screen, 3, 21, 2));
        Assert.True(screen.FieldExitPending);
    }

    [Fact(DisplayName = "field exit keys: auto-enter sends Enter when the field is completed")]
    public void AutoEnterSendsEnter()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 2, 0x4080)]);
        var result = KeyboardTestData.Press(screen, "12");
        Assert.Equal([KeyboardTestData.Aid(KeyboardTestData.AidCode("Enter"))], result.Actions);
        Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
    }

    [Fact(DisplayName = "field exit keys: Dup fills the rest of the field when allowed")]
    public void DupFillsTheRest()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 4, 0x5000), new(4, 21, 4, ALPHA)]);
        KeyboardTestData.Press(screen, "A", Fn("Dup"));
        Assert.Equal(B(0xc1, 0x1c, 0x1c, 0x1c), KeyboardTestData.Raw(screen, 3, 21, 4));
        Assert.Equal(new Position(4, 21), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("Dup"));
        Assert.Equal(OperatorError.DupNotAllowed, KeyboardTestData.ErrorCode(screen));
    }
}

public class EraseAndDeleteTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "erase and delete: Erase EOF nulls from the cursor, and Delete shifts left")]
    public void EraseEofAndDelete()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 5, ALPHA)]);
        KeyboardTestData.Press(screen, "ABCDE", At(3, 22), Fn("Delete"));
        Assert.Equal(B(Ebcdic.Encode("ACDE"), 0), KeyboardTestData.Raw(screen, 3, 21, 5));
        KeyboardTestData.Press(screen, At(3, 23), Fn("EraseEof"));
        Assert.Equal(B(Ebcdic.Encode("AC"), 0, 0, 0), KeyboardTestData.Raw(screen, 3, 21, 5));
        Assert.Equal(new Position(3, 23), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "erase and delete: Backspace deletes to the left, and at a field start goes to the previous field")]
    public void BackspaceDeletesToTheLeft()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA), new(4, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, At(4, 21), "XY", Fn("Backspace"));
        Assert.Equal(B(0xe7, 0, 0), KeyboardTestData.Raw(screen, 4, 21, 3));
        KeyboardTestData.Press(screen, Fn("Backspace"), Fn("Backspace"));
        Assert.Equal(new Position(3, 23), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "erase and delete: Erase Input clears only modified input fields and keeps their MDT")]
    public void EraseInputClearsModifiedFields()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA), new(4, 21, 3, ALPHA)]);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x00, 0x11, 4, 21, Ebcdic.Encode("OLD")));
        KeyboardTestData.Press(screen, "NEW", Fn("EraseInput"));
        Assert.Equal(B(0, 0, 0), KeyboardTestData.Raw(screen, 3, 21, 3));
        Assert.Equal("OLD", KeyboardTestData.Text(screen, 4, 21, 3));
        Assert.Equal(0x0800, screen.Fields[0].Ffw & 0x0800);
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
    }
}

public class CursorMovementTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    private static Screen Layout() =>
        KeyboardTestData.ScreenWith(
        [
            new(3, 21, 3, ALPHA),
            new(4, 21, 3, 0x6000), // bypass
            new(6, 10, 3, ALPHA),
        ]);

    [Fact(DisplayName = "cursor movement: Tab and Backtab skip bypass fields and wrap")]
    public void TabAndBacktab()
    {
        var screen = Layout();
        KeyboardTestData.Press(screen, Fn("Tab"));
        Assert.Equal(new Position(6, 10), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("Tab"));
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("Backtab"));
        Assert.Equal(new Position(6, 10), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, At(6, 11), Fn("Backtab"));
        Assert.Equal(new Position(6, 10), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "cursor movement: arrows wrap around the screen; Home and New Line find input fields")]
    public void ArrowsWrap()
    {
        var screen = Layout();
        KeyboardTestData.Press(screen, At(1, 1), Fn("Left"));
        Assert.Equal(new Position(24, 80), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("Down"));
        Assert.Equal(new Position(1, 80), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("Home"));
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("NewLine"));
        Assert.Equal(new Position(6, 10), KeyboardTestData.Cursor(screen));
        KeyboardTestData.Press(screen, Fn("NewLine"));
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "cursor movement: Home uses the IC address when there is one, and sends Record Backspace when already there")]
    public void HomeUsesTheIcAddress()
    {
        var screen = KeyboardTestData.Apply(
            new Screen(),
            B(ESC, 0x40),
            B(ESC, 0x11, 0x00, 0x08, 0x11, 3, 20, 0x1d, 0x40, 0x00, 0x24, 0, 3, 0x13, 10, 10),
            READ_MDT);
        KeyboardTestData.Press(screen, Fn("Tab"));
        Assert.Empty(KeyboardTestData.Press(screen, Fn("Home")).Actions);
        Assert.Equal(new Position(10, 10), KeyboardTestData.Cursor(screen));
        Assert.Equal(
            [KeyboardTestData.Aid(KeyboardTestData.AidCode("RecordBackspace"))],
            KeyboardTestData.Press(screen, Fn("Home")).Actions);
        Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
    }

    [Fact(DisplayName = "cursor movement: End goes after the last character in the field")]
    public void EndGoesAfterTheLastCharacter()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 6, ALPHA)]);
        KeyboardTestData.Press(screen, "AB", Fn("Home"), Fn("End"));
        Assert.Equal(new Position(3, 23), KeyboardTestData.Cursor(screen));
    }
}

public class MandatoryFieldTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "mandatory fields: a mandatory-fill field must be left empty or full")]
    public void MandatoryFill()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, 0x4007), new(4, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, "A", Fn("FieldExit"));
        Assert.Equal(OperatorError.MandatoryFill, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), Fn("Tab"));
        Assert.Equal(OperatorError.MandatoryFill, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), Fn("Enter"));
        Assert.Equal(OperatorError.MandatoryFill, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), "BC");
        Assert.Null(screen.Error);
        Assert.Equal(new Position(4, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "mandatory fields: a mandatory-enter field cannot be exited unentered, nor skipped when sending data")]
    public void MandatoryEnter()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, 0x4008), new(4, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, Fn("FieldExit"));
        Assert.Equal(OperatorError.MandatoryEnterExit, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), Fn("Tab"), "X", Fn("Enter"));
        Assert.Equal(OperatorError.MandatoryEnter, KeyboardTestData.ErrorCode(screen));
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "mandatory fields: with nothing modified, Enter goes even with an empty mandatory-enter field")]
    public void EnterGoesWithNothingModified()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, 0x4008)]);
        Assert.Equal([KeyboardTestData.Aid(0xf1)], KeyboardTestData.Press(screen, Fn("Enter")).Actions);
    }
}

public class RightAdjustFieldTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "right-adjust fields: refuse AID keys while being keyed (0020) until Field Exit adjusts them")]
    public void RefuseAidKeysWhileKeyed()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 5, 0x4006), new(4, 21, 5, ALPHA)]);
        KeyboardTestData.Press(screen, "AB", Fn("Enter"));
        Assert.Equal(OperatorError.EnterNotAllowed, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), Fn("F3"));
        Assert.Equal(OperatorError.EnterNotAllowed, KeyboardTestData.ErrorCode(screen));
        KeyboardTestData.Press(screen, Fn("Reset"), At(3, 23), Fn("FieldExit"));
        Assert.Equal("   AB", KeyboardTestData.Text(screen, 3, 21, 5));
        Assert.Equal([KeyboardTestData.Aid(KeyboardTestData.AidCode("Enter"))], KeyboardTestData.Press(screen, Fn("Enter")).Actions);
    }

    [Fact(DisplayName = "right-adjust fields: do not block keys that check no fields, nor signed numeric fields")]
    public void DoNotBlockOtherKeys()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 5, 0x4006), new(4, 21, 5, 0x4700)]);
        KeyboardTestData.Press(screen, "AB");
        Assert.Equal([KeyboardTestData.Aid(KeyboardTestData.AidCode("Help"))], KeyboardTestData.Press(screen, Fn("Help")).Actions);
        var signed = KeyboardTestData.ScreenWith([new(3, 21, 5, 0x4700)]);
        Assert.Equal(
            [KeyboardTestData.Aid(KeyboardTestData.AidCode("Enter"))],
            KeyboardTestData.Press(signed, "12", Fn("Enter")).Actions);
    }
}

public class AidKeyTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "AID keys: lock the keyboard and go at once when a READ is pending")]
    public void LockAndGoAtOnce()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, Fn("Insert"));
        var result = KeyboardTestData.Press(screen, Fn("F3"));
        Assert.Equal([KeyboardTestData.Aid(0x33)], result.Actions);
        Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
        Assert.False(screen.InsertMode);
    }

    [Fact(DisplayName = "AID keys: wait for a READ when none is pending")]
    public void WaitForARead()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)], read: false);
        Assert.Empty(KeyboardTestData.Press(screen, Fn("F24")).Actions);
        Assert.Equal(0xbc, screen.HeldAid);
    }

    [Fact(DisplayName = "AID keys: map every key to its table 16-1 code")]
    public void MapEveryKey()
    {
        int Code(string name) => KeyboardTestData.AidCode(name);
        Assert.Equal([0x31, 0x3c, 0xb1, 0xbc], new[] { Code("F1"), Code("F12"), Code("F13"), Code("F24") });
        Assert.Equal([0xf5, 0xf4, 0xf3, 0xbd], new[] { Code("RollUp"), Code("RollDown"), Code("Help"), Code("Clear") });
    }

    [Fact(DisplayName = "AID keys: follow the SOH PF key masks: PF1 is bit 7 of byte 7, PF24 bit 0 of byte 5")]
    public void FollowTheSohPfKeyMasks()
    {
        int Code(string name) => KeyboardTestData.AidCode(name);
        var screen = KeyboardTestData.ScreenWith([], header: [0, 0, 0, 24, 0x80, 0x00, 0x01]);
        Assert.False(Keyboard.AidSendsData(screen, Code("F1")));
        Assert.True(Keyboard.AidSendsData(screen, Code("F2")));
        Assert.False(Keyboard.AidSendsData(screen, Code("F24")));
        Assert.True(Keyboard.AidSendsData(screen, Code("F23")));
        Assert.True(Keyboard.AidSendsData(screen, Code("Enter")));
        Assert.False(Keyboard.AidSendsData(screen, Code("Help")));
        var shortHeader = KeyboardTestData.ScreenWith([], header: [0, 0, 0, 24]);
        Assert.True(Keyboard.AidSendsData(shortHeader, Code("F1")));
    }
}

public class KeyboardStateTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "keyboard states: queues keys typed while locked, and drains them after the keyboard unlocks")]
    public void QueuesKeysTypedWhileLocked()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA), new(4, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, Fn("Enter"));
        KeyboardTestData.Press(screen, "AB", Fn("Enter"), "C");
        Assert.Equal(4, screen.TypeAhead.Count);
        Assert.Equal(4, screen.Snapshot().TypeAhead);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x08), READ_MDT);
        var drained = Keyboard.DrainTypeAhead(screen);
        Assert.Equal("AB", KeyboardTestData.Text(screen, 3, 21, 2));
        Assert.Equal([KeyboardTestData.Aid(0xf1)], drained.Actions);
        // The keys after the second Enter wait for the next unlock.
        Assert.Equal([Char("C")], screen.TypeAhead);
    }

    [Fact(DisplayName = "keyboard states: Reset empties the type-ahead buffer, which holds 32 keystrokes")]
    public void ResetEmptiesTheTypeAheadBuffer()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, Fn("Enter"));
        KeyboardTestData.Press(screen, new string('x', 32));
        Assert.True(KeyboardTestData.Press(screen, "y").Alarm);
        Assert.Equal(32, screen.TypeAhead.Count);
        KeyboardTestData.Press(screen, Fn("Reset"));
        Assert.Empty(screen.TypeAhead);
        Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
    }

    [Fact(DisplayName = "keyboard states: an operator error empties the type-ahead buffer")]
    public void AnOperatorErrorEmptiesTheBuffer()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, 0x4500)]);
        KeyboardTestData.Press(screen, Fn("Enter"), "A1");
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x08), READ_MDT);
        Keyboard.DrainTypeAhead(screen);
        Assert.Equal(OperatorError.DigitsOnly, KeyboardTestData.ErrorCode(screen));
        Assert.Empty(screen.TypeAhead);
    }

    [Fact(DisplayName = "keyboard states: Help in an operator error sends the packed code, then waits for the host")]
    public void HelpInAnOperatorError()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, 0x4500)]);
        KeyboardTestData.Press(screen, "A");
        Assert.Equal([new KeyAction.Help([0x00, 0x10])], KeyboardTestData.Press(screen, Fn("Help")).Actions);
        Assert.True(screen.Error?.Help);
        Assert.True(KeyboardTestData.Press(screen, Fn("Reset")).Alarm);
        Assert.True(KeyboardTestData.Press(screen, Fn("Tab")).Alarm);
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
    }

    [Fact(DisplayName = "keyboard states: Attn works in every state; SysReq except in an error")]
    public void AttnWorksInEveryState()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        Assert.Equal([new KeyAction.Attention()], KeyboardTestData.Press(screen, Fn("Attention")).Actions);
        KeyboardTestData.Press(screen, Fn("Enter"));
        Assert.Equal(
            [new KeyAction.Attention(), new KeyAction.SystemRequest()],
            KeyboardTestData.Press(screen, Fn("Attention"), Fn("SystemRequest")).Actions);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x08), READ_MDT);
        KeyboardTestData.Press(screen, At(1, 1), "x");
        var sysreq = KeyboardTestData.Press(screen, Fn("SystemRequest"));
        Assert.True(sysreq.Alarm);
        Assert.Empty(sysreq.Actions);
        Assert.Equal([new KeyAction.Attention()], KeyboardTestData.Press(screen, Fn("Attention")).Actions);
    }
}

public class LeavingAnErrorTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    // Any key except Help, Attn and SysReq clears the error and then does its normal job.
    // The 5494 would need Reset (page 16-3).
    private static readonly FieldSpec[] FIELDS = [new(3, 21, 3, ALPHA), new(5, 21, 3, 0x4500), new(7, 21, 3, ALPHA)];

    /// <summary>0005: x typed on row 1, column 1, outside every field.</summary>
    private static Screen ProtectedError()
    {
        var screen = KeyboardTestData.ScreenWith(FIELDS);
        KeyboardTestData.Press(screen, At(1, 1), "x");
        Assert.Equal(OperatorError.CursorProtected, KeyboardTestData.ErrorCode(screen));
        return screen;
    }

    /// <summary>0010: a letter typed in the digits-only field, after the given digits.</summary>
    private static Screen DigitsError(string digits = "")
    {
        var screen = KeyboardTestData.ScreenWith(FIELDS);
        KeyboardTestData.Press(screen, At(5, 21), $"{digits}A");
        Assert.Equal(OperatorError.DigitsOnly, KeyboardTestData.ErrorCode(screen));
        return screen;
    }

    private static void ExpectCleared(Screen screen, KeyResult result)
    {
        Assert.Null(screen.Error);
        Assert.False(result.Alarm);
        Assert.True(result.Changed);
    }

    [Fact(DisplayName = "leaving an error, as in ACS: Tab and Backtab clear it and move to the next or previous input field")]
    public void TabAndBacktabClearIt()
    {
        var screen = ProtectedError();
        ExpectCleared(screen, KeyboardTestData.Press(screen, Fn("Tab")));
        Assert.Equal(new Position(3, 21), KeyboardTestData.Cursor(screen));
        screen = ProtectedError();
        ExpectCleared(screen, KeyboardTestData.Press(screen, Fn("Backtab")));
        Assert.Equal(new Position(7, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "leaving an error, as in ACS: the arrows and a click clear it and move the cursor")]
    public void ArrowsAndClicksClearIt()
    {
        var screen = ProtectedError();
        ExpectCleared(screen, KeyboardTestData.Press(screen, Fn("Down")));
        Assert.Equal(new Position(2, 1), KeyboardTestData.Cursor(screen));
        screen = ProtectedError();
        ExpectCleared(screen, KeyboardTestData.Press(screen, At(7, 22)));
        Assert.Equal(new Position(7, 22), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "leaving an error, as in ACS: a character clears it and is typed, and a new error is posted as usual")]
    public void ACharacterClearsIt()
    {
        var screen = DigitsError();
        ExpectCleared(screen, KeyboardTestData.Press(screen, "4"));
        Assert.Equal("4", KeyboardTestData.Text(screen, 5, 21, 1));
        Assert.True(KeyboardTestData.Press(screen, "B").Alarm);
        Assert.Equal(OperatorError.DigitsOnly, KeyboardTestData.ErrorCode(screen));
    }

    [Fact(DisplayName = "leaving an error, as in ACS: Field Exit clears it and exits the field")]
    public void FieldExitClearsIt()
    {
        var screen = DigitsError("1");
        ExpectCleared(screen, KeyboardTestData.Press(screen, Fn("FieldExit")));
        Assert.Equal(B(0xf1, 0, 0), KeyboardTestData.Raw(screen, 5, 21, 3));
        Assert.Equal(new Position(7, 21), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "leaving an error, as in ACS: Enter, an F-key and Page Down clear it and send their AID at once")]
    public void AidKeysClearItAndSend()
    {
        foreach (var key in new[] { "Enter", "F5", "RollUp" })
        {
            var screen = ProtectedError();
            var result = KeyboardTestData.Press(screen, Fn(key));
            ExpectCleared(screen, result);
            Assert.Equal([KeyboardTestData.Aid(KeyboardTestData.AidCode(key))], result.Actions);
            Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
        }
    }

    [Fact(DisplayName = "leaving an error, as in ACS: Reset only clears it, and SysReq still sounds the alarm")]
    public void ResetOnlyClearsIt()
    {
        var screen = ProtectedError();
        var sysreq = KeyboardTestData.Press(screen, Fn("SystemRequest"));
        Assert.True(sysreq.Alarm);
        Assert.Empty(sysreq.Actions);
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
        ExpectCleared(screen, KeyboardTestData.Press(screen, Fn("Reset")));
        Assert.Equal(new Position(1, 1), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "leaving an error, as in ACS: clears a host error the same way and restores the error line")]
    public void ClearsAHostError()
    {
        var screen = KeyboardTestData.ScreenWith(FIELDS);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x00, 0x11, 24, 1, Ebcdic.Encode("F3=Exit")));
        var errorLine = KeyboardTestData.Raw(screen, 24, 1, 80);
        KeyboardTestData.Apply(screen, B(ESC, 0x21, Ebcdic.Encode("Roll past the last record.")), READ_MDT);
        Assert.Equal(ErrorKind.Host, screen.Error?.Kind);
        Assert.Equal("Roll past", KeyboardTestData.Text(screen, 24, 1, 9));
        var result = KeyboardTestData.Press(screen, Fn("RollUp"));
        ExpectCleared(screen, result);
        Assert.Equal([KeyboardTestData.Aid(KeyboardTestData.AidCode("RollUp"))], result.Actions);
        Assert.Equal(errorLine, KeyboardTestData.Raw(screen, 24, 1, 80));
    }
}

public class HostCommandsAndKeyboardTests
{
    private const int ALPHA = KeyboardTestData.ALPHA;

    [Fact(DisplayName = "host commands and the keyboard: a WTD that resets MDTs or unlocks clears a held AID")]
    public void AWtdClearsAHeldAid()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)], read: false);
        KeyboardTestData.Press(screen, Fn("Enter"));
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x20, 0x00));
        Assert.Null(screen.HeldAid);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x08));
        KeyboardTestData.Press(screen, Fn("Enter"));
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x08));
        Assert.Null(screen.HeldAid);
    }

    [Fact(DisplayName = "host commands and the keyboard: an unlock during an operator error waits until a key clears the error")]
    public void AnUnlockDuringAnErrorWaits()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, At(1, 1), "x");
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x00, 0x11, 5, 21, 0x1d, 0x40, 0x00, 0x24, 0, 3));
        Assert.True(screen.KeyboardLocked);
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x00, 0x08));
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
        // Tab clears the error, the unlock homes the cursor, then Tab moves on from there.
        KeyboardTestData.Press(screen, Fn("Tab"));
        Assert.Equal(KeyboardState.Unlocked, screen.KeyboardState);
        Assert.Equal(new Position(5, 22), KeyboardTestData.Cursor(screen));
    }

    [Fact(DisplayName = "host commands and the keyboard: a key that clears an error while the host holds the keyboard is typed ahead")]
    public void AKeyThatClearsAnErrorIsTypedAhead()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)]);
        KeyboardTestData.Press(screen, At(1, 1), "x");
        KeyboardTestData.Apply(screen, B(ESC, 0x11, 0x20, 0x00));
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
        KeyboardTestData.Press(screen, Fn("Tab"));
        Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
        Assert.Equal([Fn("Tab")], screen.TypeAhead);
    }

    [Fact(DisplayName = "host commands and the keyboard: CLEAR UNIT clears an error, insert mode and a held AID; CLEAR FORMAT TABLE keeps the error")]
    public void ClearUnitClearsAnError()
    {
        var screen = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)], read: false);
        KeyboardTestData.Press(screen, Fn("Insert"), At(1, 1), "x");
        KeyboardTestData.Apply(screen, B(ESC, 0x50));
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
        KeyboardTestData.Apply(screen, B(ESC, 0x40));
        Assert.Null(screen.Error);
        Assert.False(screen.InsertMode);
        var held = KeyboardTestData.ScreenWith([new(3, 21, 3, ALPHA)], read: false);
        KeyboardTestData.Press(held, Fn("Enter"));
        KeyboardTestData.Apply(held, B(ESC, 0x50));
        Assert.Null(held.HeldAid);
    }
}
