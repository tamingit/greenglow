using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

internal static class CommandsTestData
{
    public static string Row(Screen screen, int n) =>
        Ebcdic.Decode(screen.Buffer.AsSpan((n - 1) * screen.Columns, screen.Columns).ToArray().Select(b => b < 0x40 ? (byte)0x40 : b).ToArray());

    /// <summary>A 24 x 80 screen with an error line on row 24 and one input field.</summary>
    public static Screen SimpleScreen()
    {
        var screen = new Screen();
        Put(screen, CLEAR, Wtd(0, 0x08, Sba(2, 1), Sf(0x4000, 0x20, 5), Sba(24, 1), Text("Old message")), READ_MDT);
        return screen;
    }
}

public class WriteErrorCodeTests
{
    private static string Row(Screen screen, int n) => CommandsTestData.Row(screen, n);

    [Fact(DisplayName = "WRITE ERROR CODE: writes the error line without the alarm, and the next key restores the line and acts")]
    public void WritesTheErrorLine()
    {
        var screen = CommandsTestData.SimpleScreen();
        Type(screen, Fn("Enter"));
        Assert.Equal(KeyboardState.Locked, screen.KeyboardState);
        // The host answers the Enter with an error message and a new READ.
        var result = Put(screen, B(ESC, 0x21, 0x2a, Text("Bad value"), 0x27), READ_MDT);
        Assert.DoesNotContain(new ConnectionEvent.Alarm(), result.Events);
        Assert.Equal(KeyboardState.Error, screen.KeyboardState);
        Assert.Equal(new ErrorState(ErrorKind.Host, 0, false), screen.Error);
        Assert.Equal("Bad value", Row(screen, 24).Trim());
        // As in ACS, the key clears the error, then is typed.
        Type(screen, Typed("x"));
        Assert.Equal(KeyboardState.Unlocked, screen.KeyboardState);
        Assert.Equal("Old message", Row(screen, 24).Trim());
        Assert.Equal("x", Row(screen, 2).Trim());
    }

    [Fact(DisplayName = "WRITE ERROR CODE: keeps the first saved line when a second error arrives")]
    public void KeepsTheFirstSavedLine()
    {
        var screen = CommandsTestData.SimpleScreen();
        Put(screen, B(ESC, 0x21, Text("First")));
        Put(screen, B(ESC, 0x21, Text("Second")));
        Assert.Equal("Second", Row(screen, 24).Trim());
        Type(screen, Fn("Reset"));
        Assert.Equal("Old message", Row(screen, 24).Trim());
    }

    [Fact(DisplayName = "WRITE ERROR CODE: writes a separate message line (row 25) and moves the cursor for an IC order")]
    public void WritesASeparateMessageLine()
    {
        var screen = new Screen();
        Put(screen, SIGN_ON);
        Put(screen, B(ESC, 0x21, 0x13, 7, 53, Text("CPF0000 Message")));
        var line = screen.Snapshot().MessageLine ?? [];
        Assert.Equal("CPF0000 Message", Ebcdic.Decode(line.AsSpan(0, 15)));
        Assert.Equal(6 * 80 + 52, screen.Cursor);
        Assert.Null(screen.InsertCursor);
        Type(screen, Fn("Reset"));
        Assert.True(screen.Snapshot().MessageLine?.All(b => b == 0));
    }

    [Fact(DisplayName = "WRITE ERROR CODE: limits WRITE ERROR CODE TO WINDOW to its columns")]
    public void LimitsToTheWindow()
    {
        var screen = CommandsTestData.SimpleScreen();
        Put(screen, B(ESC, 0x22, 5, 8, Text("ABCDEFG")));
        Assert.Equal("Old ABCDage ", Row(screen, 24)[..12]);
        var separate = new Screen();
        Put(separate, SIGN_ON);
        Assert.Equal("data-stream-error", Put(separate, B(ESC, 0x22, 5, 8, Text("X"))).Events[0].Type);
    }

    [Fact(DisplayName = "WRITE ERROR CODE: Help after a host error sends columns 2 to 5 of the error line, dezoned and packed")]
    public void HelpAfterAHostError()
    {
        var connection = SignOnConnection();
        connection.Receive(HostRecord(3, B(ESC, 0x21, 0x2a, Text("0123 text"))));
        Assert.Equal(
            [new Outbound(OutboundKind.Input, Records.EncodeClientRecord(Opcode.NoOperation, RecordFlag.Hlp, [0x01, 0x23]))],
            PressAll(connection, Fn("Help")).Send);
    }
}

public class RollTests
{
    private static Screen Numbered()
    {
        var screen = new Screen();
        Put(screen, CLEAR, Wtd(0, 0, new[] { 1, 2, 3, 4, 5 }.Select(n => B(Sba(n, 1), Text($"L{n}"))).ToArray()));
        return screen;
    }

    private static List<string> Rows(Screen screen) =>
        new[] { 1, 2, 3, 4, 5 }.Select(n => CommandsTestData.Row(screen, n).Trim()).ToList();

    [Fact(DisplayName = "ROLL: rolls lines up and down within the area, leaving vacated lines as they were")]
    public void RollsLinesUpAndDown()
    {
        var up = Numbered();
        Put(up, B(ESC, 0x23, 0x01, 2, 4));
        Assert.Equal(["L1", "L3", "L4", "L4", "L5"], Rows(up));
        var down = Numbered();
        Put(down, B(ESC, 0x23, 0x82, 1, 5));
        Assert.Equal(["L1", "L2", "L1", "L2", "L3"], Rows(down));
    }

    [Fact(DisplayName = "ROLL: rejects the four parameter errors and ignores a count of zero")]
    public void RejectsParameterErrors()
    {
        foreach (var (parameter, top, bottom) in new[] { (0x01, 0, 4), (0x01, 2, 25), (0x01, 4, 4), (0x05, 2, 4) })
        {
            Assert.Equal("data-stream-error", Put(Numbered(), B(ESC, 0x23, parameter, top, bottom)).Events[0].Type);
        }
        var screen = Numbered();
        Assert.Empty(Put(screen, B(ESC, 0x23, 0x00, 1, 5)).Events);
        Assert.Equal("L1", CommandsTestData.Row(screen, 1).Trim());
    }
}

public class SaveAndRestoreScreenTests
{
    [Fact(DisplayName = "SAVE and RESTORE SCREEN: round-trips the whole display state")]
    public void RoundTripsTheWholeState()
    {
        var screen = new Screen();
        Put(screen, SIGN_ON);
        Type(screen, Typed("user1"), Fn("FieldExit"), Typed("pass"), Fn("Insert"));
        Put(screen, B(ESC, 0x21, Text("A message")));
        var before = screen.Snapshot();
        var saved = SavedScreens.Encode(screen);
        var other = new Screen();
        Put(other, CLEAR, Wtd(0, 0x08, Text("something else")));
        Put(other, B(ESC, 0x12, saved));
        Assert.Equal(before, other.Snapshot());
        Type(other, Fn("Reset"));
        Assert.Equal(KeyboardState.Unlocked, other.KeyboardState);
    }

    [Fact(DisplayName = "SAVE and RESTORE SCREEN: keeps a pending READ, a held AID and a deferred unlock")]
    public void KeepsPendingState()
    {
        var screen = new Screen();
        Put(screen, SIGN_ON);
        Type(screen, At(1, 1), Typed("x")); // operator error
        Put(screen, Wtd(0, 0x08));
        var clone = new Screen();
        Put(clone, B(ESC, 0x12, SavedScreens.Encode(screen)));
        Assert.Equal(new DeferredUnlock(true), clone.DeferredUnlock);
        Assert.Equal(0x52, clone.PendingRead);
        Assert.Equal(screen.Snapshot(), clone.Snapshot());
    }

    [Fact(DisplayName = "SAVE and RESTORE SCREEN: SAVE PARTIAL takes the screen as it was when the command was reached")]
    public void SavePartialTakesTheScreenAsItWas()
    {
        var connection = SignOnConnection();
        var popup = connection.Receive(HostRecord(3, B(ESC, 0x03, 0, 0, 0, 0, 0, Wtd(0, 0, Sba(12, 20), Text("Popup text")))));
        Assert.Equal([OutboundKind.HostRead], Kinds(popup));
        var reply = SentRecord(popup.Send[0].Bytes);
        Assert.Equal(Opcode.SaveScreen, reply.Opcode);
        // ESC 13, then a length that counts its own 2 bytes, then the saved data.
        var data = reply.Data;
        Assert.Equal((ESC, (byte)0x13), (data[0], data[1]));
        var length = (data[2] << 8) | data[3];
        Assert.Equal(2 + length, data.Length);
        var saved = data[4..];
        var restored = connection.Receive(
            HostRecord(Opcode.RestoreScreen, B(ESC, 0x13, length >> 8, length & 0xff, saved, READ_MDT)));
        Assert.DoesNotContain(restored.Events, e => e.Type == "data-stream-error");
        Assert.NotEqual("Popup text", Ebcdic.Decode(connection.Screen.Buffer.AsSpan(11 * 80 + 19, 10)));
        Assert.Equal(0x52, connection.Screen.PendingRead);
    }

    [Fact(DisplayName = "SAVE and RESTORE SCREEN: answers SAVE SCREEN with ESC 12 and the saved data, as a host read")]
    public void AnswersSaveScreen()
    {
        var connection = SignOnConnection();
        var save = connection.Receive(HostRecord(Opcode.SaveScreen, B(ESC, 0x02)));
        Assert.Equal([OutboundKind.HostRead], Kinds(save));
        Assert.Equal(B(4, 0, 0, 0x04, ESC, 0x12), save.Send[0].Bytes[6..12]);
        Assert.Equal(KeyboardState.Unlocked, connection.Screen.Keyboard);
    }

    [Fact(DisplayName = "SAVE and RESTORE SCREEN: insists that SAVE SCREEN ends the record")]
    public void InsistsThatSaveScreenEndsTheRecord()
    {
        var result = Put(new Screen(), B(ESC, 0x02), CLEAR);
        Assert.Equal(
            new ConnectionEvent.DataStreamError("SAVE SCREEN must be the last command in the record"),
            result.Events[0]);
        // No save reply: the command was rejected (1003 0101; the manual gives no code).
        Assert.Equal([new HostRequest.NegativeResponse(0x10030101)], result.Requests);
    }

    [Fact(DisplayName = "SAVE and RESTORE SCREEN: rejects saved data it cannot decode with 1005 0127 and leaves the screen as it was")]
    public void RejectsSavedDataItCannotDecode()
    {
        var screen = CommandsTestData.SimpleScreen();
        var before = screen.Snapshot();
        var result = Put(screen, B(ESC, 0x12, 1, 2, 3));
        Assert.Equal(
            [new ConnectionEvent.DataStreamError("saved screen not restored: it was not saved by this client")],
            result.Events);
        Assert.Equal([new HostRequest.NegativeResponse(0x10050127)], result.Requests);
        Assert.False(result.Changed);
        Assert.Equal(before, screen.Snapshot());
    }

    [Fact(DisplayName = "SAVE and RESTORE SCREEN: sends the negative response for a bad restore as a protocol reply")]
    public void SendsTheNegativeResponseForABadRestore()
    {
        var connection = SignOnConnection();
        var before = connection.Screen;
        var restored = connection.Receive(HostRecord(Opcode.RestoreScreen, B(ESC, 0x12, 1, 2, 3)));
        Assert.Equal(
            [new Outbound(OutboundKind.Protocol, Records.EncodeClientRecord(0, RecordFlag.Err, [0x10, 0x05, 0x01, 0x27]))],
            restored.Send);
        Assert.Contains(new ConnectionEvent.NegativeResponse(0x10050127), restored.Events);
        Assert.Equal(before, connection.Screen);
    }
}

public class ReadScreenAndReadImmediateTests
{
    [Fact(DisplayName = "READ SCREEN and READ IMMEDIATE: READ SCREEN returns the raw buffer with opcode 0")]
    public void ReadScreenReturnsTheRawBuffer()
    {
        var connection = SignOnConnection();
        var read = connection.Receive(HostRecord(Opcode.ReadScreen, B(ESC, 0x62)));
        Assert.Equal([OutboundKind.HostRead], Kinds(read));
        var bytes = read.Send[0].Bytes;
        Assert.Equal(Opcode.NoOperation, bytes[9]);
        Assert.Equal(10 + 24 * 80 + 2, bytes.Length);
        Assert.Equal(connection.Screen.Buffer, bytes[10..(10 + 1920)]);
    }

    [Fact(DisplayName = "READ SCREEN and READ IMMEDIATE: READ IMMEDIATE sends every field with AID 0 and leaves the pending READ and keyboard alone")]
    public void ReadImmediateSendsEveryField()
    {
        var connection = SignOnConnection();
        PressAll(connection, Typed("ab"));
        var read = connection.Receive(HostRecord(Opcode.ReadImmediate, B(ESC, 0x72)));
        Assert.Equal([OutboundKind.HostRead], Kinds(read));
        var data = Body(read.Send[0].Bytes);
        Assert.Equal(B(6, 55, 0x00), data[..3]);
        Assert.Equal(B(Text("AB"), Enumerable.Repeat(0x40, 8)), data[3..13]);
        Assert.Equal(3 + 5 * 10, data.Length);
        Assert.Equal(0x52, connection.Screen.PendingRead);
        Assert.Equal(KeyboardState.Unlocked, connection.Screen.Keyboard);
    }

    [Fact(DisplayName = "READ SCREEN and READ IMMEDIATE: READ MODIFIED IMMEDIATE ALTERNATE sends the modified fields SBA-delimited")]
    public void ReadModifiedImmediateAlternate()
    {
        var connection = SignOnConnection();
        PressAll(connection, Typed("ab"));
        var read = connection.Receive(HostRecord(Opcode.ReadImmediate, B(ESC, 0x83)));
        Assert.Equal(B(6, 55, 0x00, Sba(6, 53), Text("AB")), Body(read.Send[0].Bytes));
    }

    [Fact(DisplayName = "READ SCREEN and READ IMMEDIATE: insists that the host reads end the record")]
    public void InsistsThatHostReadsEndTheRecord()
    {
        Assert.Equal(
            "READ SCREEN must be the last command in the record",
            Assert.IsType<ConnectionEvent.DataStreamError>(Put(new Screen(), B(ESC, 0x62), CLEAR).Events[0]).Message);
        Assert.Equal(
            "READ IMMEDIATE must be the last command in the record",
            Assert.IsType<ConnectionEvent.DataStreamError>(Put(new Screen(), B(ESC, 0x72), CLEAR).Events[0]).Message);
    }

    [Fact(DisplayName = "READ SCREEN and READ IMMEDIATE: are refused by nothing in the engine, but tagged so tools can refuse them")]
    public void AreTaggedSoToolsCanRefuseThem()
    {
        var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC" });
        Assert.Empty(connection.Key(Fn("Enter")).Send);
    }
}
