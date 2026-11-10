using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

public class ConnectionTests
{
    internal static readonly byte[] QUERY = Telnet.EncodeRecord(
        B(0x00, 0x11, 0x12, 0xa0, 0, 0, 4, 0, 0, 0x03, 0x04, 0xf3, 0x00, 0x05, 0xd9, 0x70, 0x00));

    private static Connection New(string? deviceName = "GGTEST001", string terminalType = "IBM-3477-FC") =>
        new(new ConnectionConfig { TerminalType = terminalType, DeviceName = deviceName });

    [Fact(DisplayName = "Connection: negotiates, reports the startup response and answers the QUERY")]
    public void NegotiatesAndAnswersTheQuery()
    {
        var connection = New();

        var negotiation = connection.Receive(NEGOTIATION);
        Assert.Equal([OutboundKind.Negotiation], Kinds(negotiation).Distinct());
        Assert.Equal(["negotiated"], Types(negotiation));

        var startup = connection.Receive(StartupResponse("I902", "GGTEST001"));
        Assert.Equal(
            [new ConnectionEvent.Startup(new StartupResult("I902", "SYSTEM", "GGTEST001", true, "Session started"))],
            startup.Events);

        var query = connection.Receive(QUERY);
        Assert.Equal([OutboundKind.QueryReply], Kinds(query));
        Assert.Equal(["record", "query-answered"], Types(query));
    }

    [Fact(DisplayName = "Connection: reports device-in-use after a collision once the connection closes")]
    public void ReportsDeviceInUseAfterACollision()
    {
        var connection = New();
        connection.Receive(NEGOTIATION[..14]);
        var collision = connection.Receive(B(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("DEVNAME"), IAC, SE));
        Assert.Equal([OutboundKind.Negotiation], Kinds(collision));
        Assert.Equal([new ConnectionEvent.DeviceInUse("GGTEST001")], connection.Closed());
        Assert.Empty(connection.Closed());
    }

    [Fact(DisplayName = "Connection: reports device-in-use for startup code 8902, only once")]
    public void ReportsDeviceInUseFor8902()
    {
        var connection = New();
        connection.Receive(NEGOTIATION);
        Assert.Equal(["startup", "device-in-use"], Types(connection.Receive(StartupResponse("8902", "GGTEST001"))));
        Assert.Empty(connection.Closed());
    }

    [Fact(DisplayName = "Connection: warns when the IBM i used a different device")]
    public void WarnsAboutADifferentDevice()
    {
        var connection = New();
        connection.Receive(NEGOTIATION);
        Assert.Contains(
            new ConnectionEvent.DeviceMismatch("GGTEST001", "QPADEV0001"),
            connection.Receive(StartupResponse("I902", "QPADEV0001")).Events);
    }

    [Fact(DisplayName = "Connection: treats a record before negotiation as a protocol error")]
    public void TreatsAnEarlyRecordAsAProtocolError()
    {
        var connection = New(null);
        var result = connection.Receive(QUERY);
        Assert.Equal([new ConnectionEvent.ProtocolError("5250 record before negotiation finished")], result.Events);
        Assert.Empty(result.Send);
    }

    [Fact(DisplayName = "Connection: rejects an unknown terminal type")]
    public void RejectsAnUnknownTerminalType()
    {
        var error = Assert.Throws<ArgumentException>(() => New(null, "IBM-9999-1"));
        Assert.Contains("Unsupported terminal type", error.Message);
    }
}

public class KeysAndRepliesTests
{
    private static byte[] Reply(params object[] data) => Records.EncodeClientRecord(Opcode.PutGet, 0, B(data));

    private static Outbound Input(byte[] bytes) => new(OutboundKind.Input, bytes);

    [Fact(DisplayName = "keys and replies: signs on: user, Field Exit, password and Enter make one READ MDT reply")]
    public void SignsOn()
    {
        var connection = SignOnConnection();
        Assert.Equal(new Position(6, 53), connection.Screen.Cursor);
        var typing = PressAll(connection, Typed("user1"), Fn("FieldExit"), Typed("secret9"));
        Assert.Empty(typing.Send);
        Assert.Contains("screen", Types(typing));
        var enter = PressAll(connection, Fn("Enter"));
        Assert.Equal(
            [Input(Reply(7, 60, 0xf1, Sba(6, 53), Text("USER1"), Sba(7, 53), Text("SECRET9")))],
            enter.Send);
        Assert.Equal(KeyboardState.Locked, connection.Screen.Keyboard);
        Assert.Null(connection.Screen.PendingRead);
    }

    [Fact(DisplayName = "keys and replies: applies the READ control characters after the reply")]
    public void AppliesTheReadControlCharacters()
    {
        var connection = SignOnConnection();
        // READ MDT FIELDS with "reset MDT on non-bypass fields" and "unlock".
        connection.Receive(HostRecord(3, B(ESC, 0x52, 0x40, 0x08)));
        PressAll(connection, Typed("a"), Fn("Enter"));
        Assert.Equal(KeyboardState.Unlocked, connection.Screen.Keyboard);
        Assert.False(connection.Screen.Fields[0].Format.Modified);
    }

    [Fact(DisplayName = "keys and replies: holds an AID pressed with no READ pending and sends it with the next READ")]
    public void HoldsAnAid()
    {
        var connection = SignOnConnection();
        PressAll(connection, Fn("Enter"));
        connection.Receive(HostRecord(3, Wtd(0, 0x08)));
        Assert.Empty(PressAll(connection, Fn("F3")).Send);
        var read = connection.Receive(HostRecord(3, READ_MDT));
        Assert.Equal([Input(Reply(6, 53, Keyboard.AidCodes["F3"]))], read.Send);
    }

    [Fact(DisplayName = "keys and replies: sends keys typed ahead once the host unlocks the keyboard and reads")]
    public void SendsKeysTypedAhead()
    {
        var connection = SignOnConnection();
        PressAll(connection, Fn("Enter"));
        Assert.Empty(PressAll(connection, Typed("ab"), Fn("Enter")).Send);
        Assert.Equal(3, connection.Screen.TypeAhead);
        var next = connection.Receive(HostRecord(3, B(Wtd(0, 0x08), READ_MDT)));
        Assert.Equal([Input(Reply(6, 55, 0xf1, Sba(6, 53), Text("AB")))], next.Send);
        Assert.Equal(0, connection.Screen.TypeAhead);
        Assert.Equal("screen", Types(next)[^1]);
    }

    [Fact(DisplayName = "keys and replies: answers Cancel Invite and forgets the pending READ")]
    public void AnswersCancelInvite()
    {
        var connection = SignOnConnection();
        var cancel = connection.Receive(HostRecord(Opcode.CancelInvite, []));
        Assert.Equal([new Outbound(OutboundKind.Protocol, Records.EncodeClientRecord(Opcode.CancelInvite, 0, []))], cancel.Send);
        Assert.Null(connection.Screen.PendingRead);
        Assert.Empty(PressAll(connection, Fn("Enter")).Send);
    }

    [Fact(DisplayName = "keys and replies: answers a QUERY that is not first in its record")]
    public void AnswersALateQuery()
    {
        var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC" });
        connection.Receive(NEGOTIATION);
        var query = connection.Receive(HostRecord(3, B(CLEAR, ESC, 0xf3, 0x00, 0x05, 0xd9, 0x70, 0x00)));
        Assert.Equal([OutboundKind.QueryReply], Kinds(query));
        Assert.Contains("query-answered", Types(query));
    }

    [Fact(DisplayName = "keys and replies: sends Attention, System Request and Help in error state as header flags")]
    public void SendsSignalsAsHeaderFlags()
    {
        var connection = SignOnConnection();
        Assert.Equal(
            [
                Input(Records.EncodeClientRecord(Opcode.NoOperation, RecordFlag.Atn, [])),
                Input(Records.EncodeClientRecord(Opcode.NoOperation, RecordFlag.Srq, [])),
            ],
            PressAll(connection, Fn("Attention"), Fn("SystemRequest")).Send);
        var error = PressAll(connection, At(1, 1), Typed("x"));
        Assert.Contains("alarm", Types(error));
        Assert.Equal(
            [Input(Records.EncodeClientRecord(Opcode.NoOperation, RecordFlag.Hlp, [0x00, 0x05]))],
            PressAll(connection, Fn("Help")).Send);
    }

    [Fact(DisplayName = "keys and replies: reports a stuck keyboard only when a key waits while the host waits for input")]
    public void ReportsAStuckKeyboard()
    {
        var connection = SignOnConnection();
        PressAll(connection, Fn("Enter"));
        // A READ that leaves the keyboard locked is often followed by a record that unlocks it.
        var read = connection.Receive(HostRecord(3, B(Wtd(0, 0), READ_MDT)));
        Assert.DoesNotContain("keyboard-stuck", Types(read));
        Assert.Contains("keyboard-stuck", Types(PressAll(connection, Typed("a"))));
        Assert.DoesNotContain("keyboard-stuck", Types(PressAll(connection, Typed("b"))));
        var unlock = connection.Receive(HostRecord(3, B(Wtd(0, 0x08), READ_MDT)));
        Assert.DoesNotContain("keyboard-stuck", Types(unlock));
        Assert.DoesNotContain("keyboard-stuck", Types(PressAll(connection, Typed("c"))));
    }

    [Fact(DisplayName = "keys and replies: after a host error and READ, Page Down clears the error and sends Roll Up at once")]
    public void PageDownAfterAHostError()
    {
        var connection = SignOnConnection();
        var message = Text("Roll up or down past the first or last record in file.");
        connection.Receive(HostRecord(3, B(ESC, 0x21, 0x22, message, READ_MDT)));
        Assert.Equal(KeyboardState.Error, connection.Screen.Keyboard);
        Assert.Equal(message[..4], connection.Screen.MessageLine?[1..5]);
        var page = PressAll(connection, Fn("RollUp"));
        Assert.DoesNotContain("alarm", Types(page));
        Assert.Equal([OutboundKind.Input], Kinds(page));
        var reply = SentRecord(page.Send[0].Bytes);
        Assert.Equal(Opcode.PutGet, reply.Opcode);
        Assert.Equal(Keyboard.AidCodes["RollUp"], reply.Data[2]);
        Assert.Null(connection.Screen.Error);
        Assert.Equal(new byte[80], connection.Screen.MessageLine);
    }

    [Fact(DisplayName = "keys and replies: flags field resequencing, which replies do not honour yet")]
    public void FlagsFieldResequencing()
    {
        var connection = SignOnConnection();
        connection.Receive(
            HostRecord(3, B(CLEAR, Wtd(0, 0x08, new[] { 0x01, 0x07, 0, 0, 1, 24, 0, 0, 0 }, Sba(2, 1), Sf(0x4000, 0x20, 3)), READ_MDT)));
        Assert.Contains(new ConnectionEvent.Unsupported("field resequencing"), PressAll(connection, Fn("Enter")).Events);
    }

    [Fact(DisplayName = "keys and replies: shows typed text in the next snapshot")]
    public void ShowsTypedText()
    {
        var connection = SignOnConnection();
        var typing = PressAll(connection, Typed("qpgmr"));
        var last = typing.Events.OfType<ConnectionEvent.Screen>().Last();
        Assert.Equal(new Position(6, 58), last.Snapshot.Cursor);
        var start = 5 * 80 + 52;
        Assert.Equal("QPGMR", Ebcdic.Decode(connection.Screen.Buffer.AsSpan(start, 5)));
    }
}

public class ConnectionNegativeResponseTests
{
    [Fact(DisplayName = "negative responses: rejects X'FF' with exactly these bytes, as a protocol reply")]
    public void RejectsFf()
    {
        var connection = SignOnConnection();
        PressAll(connection, Fn("Enter"));
        var result = connection.Receive(HostRecord(3, B(CLEAR, Wtd(0x20, 0x08, Sba(2, 1), Text("Before"), 0xff), READ_MDT)));
        Assert.Equal(
            [
                new Outbound(
                    OutboundKind.Protocol,
                    // Length 14, GDS 12A0, ERR flag, No Operation, 1005 0142, IAC EOR.
                    B(0x00, 0x0e, 0x12, 0xa0, 0x00, 0x00, 0x04, 0x80, 0x00, 0x00, 0x10, 0x05, 0x01, 0x42, 0xff, 0xef)),
            ],
            result.Send);
        Assert.Contains(new ConnectionEvent.DataStreamError("0xff is not a valid data character"), result.Events);
        Assert.Contains(new ConnectionEvent.NegativeResponse(0x10050142), result.Events);
        Assert.Equal(KeyboardState.Locked, connection.Screen.Keyboard);
        Assert.Null(connection.Screen.PendingRead);
    }

    [Fact(DisplayName = "negative responses: rejects a command it does not know with 1003 0101")]
    public void RejectsAnUnknownCommand()
    {
        var connection = SignOnConnection();
        var result = connection.Receive(HostRecord(3, B(ESC, 0x66)));
        Assert.Equal(
            [new Outbound(OutboundKind.Protocol, Records.EncodeClientRecord(0, RecordFlag.Err, [0x10, 0x03, 0x01, 0x01]))],
            result.Send);
        Assert.Equal(["record", "unsupported", "negative-response"], Types(result));
    }

    [Fact(DisplayName = "negative responses: answers a QUERY earlier in the record before the negative response")]
    public void AnswersAnEarlierQueryFirst()
    {
        var connection = SignOnConnection();
        var query = B(ESC, 0xf3, 0x00, 0x05, 0xd9, 0x70, 0x00);
        var result = connection.Receive(HostRecord(3, B(query, Wtd(0, 0, 0xff))));
        Assert.Equal([OutboundKind.QueryReply, OutboundKind.Protocol], Kinds(result));
        Assert.Equal(new HostRecord(RecordFlag.Err, Opcode.NoOperation, [0x10, 0x05, 0x01, 0x42]), SentRecord(result.Send[1].Bytes));
    }

    [Fact(DisplayName = "negative responses: keeps keys typed after an error until a later record unlocks the keyboard")]
    public void KeepsKeysTypedAfterAnError()
    {
        var connection = SignOnConnection();
        PressAll(connection, Fn("Enter"));
        connection.Receive(HostRecord(3, B(CLEAR, Wtd(0x20, 0x08, 0xff), READ_MDT)));
        // No READ is pending, so the host isn't waiting: not a stuck keyboard.
        Assert.DoesNotContain("keyboard-stuck", Types(PressAll(connection, Typed("ab"))));
        Assert.Equal(2, connection.Screen.TypeAhead);
        var recovered = connection.Receive(HostRecord(3, B(CLEAR, Wtd(0x00, 0x08, Sba(2, 1), Sf(0x4000, 0x20, 5)), READ_MDT)));
        Assert.Empty(recovered.Send);
        Assert.Equal(0, connection.Screen.TypeAhead);
        Assert.Equal("ab", Ebcdic.Decode(connection.Screen.Buffer.AsSpan(81, 2)));
    }

    [Fact(DisplayName = "negative responses: sends nothing for a record whose header is not valid")]
    public void SendsNothingForABadHeader()
    {
        var connection = SignOnConnection();
        // The length field says 16 bytes, but the record has 14.
        var bad = Telnet.EncodeRecord(B(0, 16, 0x12, 0xa0, 0, 0, 4, 0, 0, 3, ESC, 0x40, 0, 0));
        var result = connection.Receive(bad);
        Assert.Empty(result.Send);
        Assert.Equal(["protocol-error"], Types(result));
    }
}
