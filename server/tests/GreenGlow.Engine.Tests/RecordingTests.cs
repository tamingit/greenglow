using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

public class RecorderTests
{
    [Fact(DisplayName = "Recorder: round-trips chunks with direction and timing")]
    public void RoundTripsChunks()
    {
        double now = 1000;
        var recorder = new Recorder(() => now);
        recorder.Add(Direction.Host, B(0xff, 0xfd, 0x27));
        now = 1250;
        recorder.Add(Direction.Client, B(0xff, 0xfb, 0x27));
        now = 1300;
        recorder.Add(Direction.Host, []);

        Assert.Equal(
            [
                new RecordedChunk(Direction.Host, 0, B(0xff, 0xfd, 0x27)),
                new RecordedChunk(Direction.Client, 250, B(0xff, 0xfb, 0x27)),
                new RecordedChunk(Direction.Host, 300, []),
            ],
            RecordingCodec.Decode(recorder.ToBytes()));
    }

    [Fact(DisplayName = "Recorder: rejects other files and truncated recordings")]
    public void RejectsOtherFilesAndTruncatedRecordings()
    {
        var notOurs = Assert.Throws<InvalidDataException>(() => RecordingCodec.Decode(B(1, 2, 3)));
        Assert.Contains("Not a GreenGlow", notOurs.Message);
        var recorder = new Recorder(() => 0);
        recorder.Add(Direction.Host, B(1, 2, 3));
        var truncated = Assert.Throws<InvalidDataException>(() => RecordingCodec.Decode(recorder.ToBytes()[..^1]));
        Assert.Contains("truncated", truncated.Message);
    }
}

internal static class RecordingTestData
{
    // The TEST password field is monocase, so the marker is uppercase: a lowercase one would
    // be uppercased on the way in and never match, proving nothing.
    public const string PASSWORD = "SECRET9";
    public const string USER = "USER1";

    public static Connection RecordedSession(Blank blank)
    {
        var connection = new Connection(
            new ConnectionConfig
            {
                TerminalType = "IBM-3477-FC",
                DeviceName = "GGTEST001",
                Recording = new RecordingOptions(() => 0, blank),
            });
        connection.Receive(NEGOTIATION);
        connection.Receive(StartupResponse("I902", "GGTEST001"));
        connection.Receive(HostRecord(Opcode.PutGet, SIGN_ON));
        return connection;
    }

    public static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Ebcdic.Encode(needle)) >= 0;

    /// <summary>Replays a recording: host chunks into a fresh connection, client records into the scrub check.</summary>
    public static (List<ConnectionEvent> Events, List<string> Problems) Replay(byte[] bytes)
    {
        var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC" });
        var client = new TelnetDecoder();
        var events = new List<ConnectionEvent>();
        var problems = new List<string>();
        foreach (var chunk in RecordingCodec.Decode(bytes))
        {
            if (chunk.Direction == Direction.Host)
            {
                events.AddRange(connection.Receive(chunk.Bytes).Events);
                continue;
            }
            foreach (var telnetEvent in client.Push(chunk.Bytes))
            {
                if (telnetEvent is not TelnetEvent.Record record) continue;
                if (Records.Parse(record.Data) is ParsedRecord.Data parsed)
                {
                    problems.AddRange(Scrub.ScrubProblems(connection.Screen, parsed.Record));
                }
            }
        }
        return (events, problems);
    }

    public static List<ConnectionEvent> Errors(IEnumerable<ConnectionEvent> events) =>
        events.Where(e => e.Type is "protocol-error" or "data-stream-error" or "unsupported").ToList();

    public static byte[] Recorded(Connection connection) => connection.Recording?.ToBytes() ?? [];
}

public class SessionRecordingTests
{
    private const string PASSWORD = RecordingTestData.PASSWORD;
    private const string USER = RecordingTestData.USER;

    [Fact(DisplayName = "session recordings: blank the typed password but keep the user profile")]
    public void BlankTheTypedPassword()
    {
        var connection = RecordingTestData.RecordedSession(Blank.Nondisplay);
        var sent = PressAll(connection, Typed(USER), Fn("FieldExit"), Typed(PASSWORD), Fn("Enter"));
        // The real send carries the password; the recording doesn't.
        Assert.True(RecordingTestData.Contains(sent.Send[0].Bytes, PASSWORD));
        var bytes = RecordingTestData.Recorded(connection);
        Assert.False(RecordingTestData.Contains(bytes, PASSWORD));
        Assert.True(RecordingTestData.Contains(bytes, USER));
        var (events, problems) = RecordingTestData.Replay(bytes);
        Assert.Empty(RecordingTestData.Errors(events));
        Assert.Empty(problems);
    }

    [Fact(DisplayName = "session recordings: blank every field with all-input")]
    public void BlankEveryField()
    {
        var connection = RecordingTestData.RecordedSession(Blank.AllInput);
        PressAll(connection, Typed(USER), Fn("FieldExit"), Typed(PASSWORD), Fn("Enter"));
        var bytes = RecordingTestData.Recorded(connection);
        Assert.False(RecordingTestData.Contains(bytes, PASSWORD));
        Assert.False(RecordingTestData.Contains(bytes, USER));
    }

    [Fact(DisplayName = "session recordings: blank a password typed ahead and sent after the keyboard unlocks")]
    public void BlankAPasswordTypedAhead()
    {
        var connection = RecordingTestData.RecordedSession(Blank.Nondisplay);
        PressAll(connection, Fn("Enter"));
        PressAll(connection, Typed(USER), Fn("FieldExit"), Typed(PASSWORD), Fn("Enter"));
        var unlocked = connection.Receive(HostRecord(Opcode.PutGet, B(Wtd(0, 0x08), READ_MDT)));
        Assert.True(RecordingTestData.Contains(unlocked.Send[0].Bytes, PASSWORD));
        var bytes = RecordingTestData.Recorded(connection);
        Assert.False(RecordingTestData.Contains(bytes, PASSWORD));
        Assert.Empty(RecordingTestData.Replay(bytes).Problems);
    }

    [Fact(DisplayName = "session recordings: keeps host and client chunks in order")]
    public void KeepsChunksInOrder()
    {
        var connection = RecordingTestData.RecordedSession(Blank.Nondisplay);
        PressAll(connection, Fn("Enter"));
        var chunks = RecordingCodec.Decode(RecordingTestData.Recorded(connection));
        var directions = chunks.Select(c => c.Direction).ToList();
        // Negotiation requests, replies; startup and sign-on; then the Enter reply.
        Assert.Equal(Direction.Host, directions[0]);
        Assert.Equal(Direction.Client, directions[^1]);
        Assert.True(directions.Count(d => d == Direction.Client) > 3);
    }

    [Fact(DisplayName = "session recordings: record host data as events that decode the same however the stream was split")]
    public void RecordHostDataAsEvents()
    {
        var stream = B(
            NEGOTIATION,
            StartupResponse("I902", "GGTEST001"),
            // 0xFF in the data exercises IAC doubling.
            HostRecord(Opcode.PutGet, Wtd(0, 0x08, new[] { 0xff })));
        List<TelnetEvent> Decode(byte[] bytes) => new TelnetDecoder().Push(bytes);
        var expected = Decode(stream);
        for (var split = 1; split < stream.Length; split++)
        {
            var connection = new Connection(
                new ConnectionConfig { TerminalType = "IBM-3477-FC", Recording = new RecordingOptions(() => 0, Blank.Nondisplay) });
            connection.Receive(stream.AsSpan(0, split));
            connection.Receive(stream.AsSpan(split));
            var host = RecordingCodec.Decode(RecordingTestData.Recorded(connection))
                .Where(c => c.Direction == Direction.Host)
                .SelectMany(c => c.Bytes)
                .ToArray();
            Assert.Equal(expected, Decode(host));
        }
    }
}

public class ScrubProblemsTests
{
    [Fact(DisplayName = "scrubProblems: finds a nondisplay field that was not blanked")]
    public void FindsAnUnblankedField()
    {
        var screen = new Screen();
        DataStream.ApplyRecord(screen, new HostRecord(0, Opcode.PutGet, SIGN_ON), true);
        Type(screen, Typed(RecordingTestData.USER), Fn("FieldExit"), Typed(RecordingTestData.PASSWORD));
        HostRecord Reply(Blank blank) =>
            new(0, Opcode.PutGet, Engine.Reply.EncodeReadReply(screen, ReadCommand.ReadMdtFields, 0xf1, blank));
        Assert.Equal(["nondisplay field at row 7, column 53 is not blanked"], Scrub.ScrubProblems(screen.Snapshot(), Reply(Blank.None)));
        Assert.Empty(Scrub.ScrubProblems(screen.Snapshot(), Reply(Blank.Nondisplay)));
        var input = new HostRecord(0, Opcode.PutGet, Engine.Reply.EncodeReadReply(screen, ReadCommand.ReadInputFields, 0xf1));
        Assert.Single(Scrub.ScrubProblems(screen.Snapshot(), input));
    }
}

public class HostReadRecordingTests
{
    private const string PASSWORD = RecordingTestData.PASSWORD;

    private static Connection HalfTypedPassword()
    {
        var connection = RecordingTestData.RecordedSession(Blank.Nondisplay);
        PressAll(connection, Typed(RecordingTestData.USER), Fn("FieldExit"), Typed(PASSWORD));
        return connection;
    }

    [Fact(DisplayName = "recordings of host reads: blank the password in a SAVE reply and in the RESTORE that echoes it")]
    public void BlankThePasswordInSaveAndRestore()
    {
        var connection = HalfTypedPassword();
        var save = connection.Receive(HostRecord(Opcode.SaveScreen, B(0x04, 0x02)));
        var saved = SentRecord(save.Send[0].Bytes).Data;
        Assert.True(RecordingTestData.Contains(save.Send[0].Bytes, PASSWORD));
        // A break message, then the host restores the screen with what the client saved.
        connection.Receive(HostRecord(Opcode.PutGet, Wtd(0, 0x08, new[] { 0x11, 1, 1 }, new[] { 0xc1 })));
        connection.Receive(HostRecord(Opcode.RestoreScreen, saved));
        Assert.True(RecordingTestData.Contains(saved, PASSWORD));
        var bytes = RecordingTestData.Recorded(connection);
        Assert.False(RecordingTestData.Contains(bytes, PASSWORD));
        var (events, problems) = RecordingTestData.Replay(bytes);
        Assert.Empty(RecordingTestData.Errors(events));
        Assert.Empty(problems);
    }

    [Fact(DisplayName = "recordings of host reads: zero saved data the parser never reached")]
    public void ZeroSavedDataNeverReached()
    {
        var connection = HalfTypedPassword();
        var save = connection.Receive(HostRecord(Opcode.SaveScreen, B(0x04, 0x02)));
        var saved = SentRecord(save.Send[0].Bytes).Data;
        // An unknown command first: the record stops there, before the restore data.
        connection.Receive(HostRecord(Opcode.RestoreScreen, B(0x04, 0x99, saved)));
        Assert.False(RecordingTestData.Contains(RecordingTestData.Recorded(connection), PASSWORD));
    }

    [Fact(DisplayName = "recordings of host reads: keep negative responses, which the scrub check passes on a screen with a password field")]
    public void KeepNegativeResponses()
    {
        var connection = RecordingTestData.RecordedSession(Blank.Nondisplay);
        var rejected = connection.Receive(HostRecord(Opcode.PutGet, Wtd(0, 0, 0xff)));
        Assert.Equal([OutboundKind.Protocol], Kinds(rejected));
        var (events, problems) = RecordingTestData.Replay(RecordingTestData.Recorded(connection));
        Assert.Contains(new ConnectionEvent.NegativeResponse(0x10050142), events);
        Assert.Empty(problems);
        // A code shaped like a READ reply (byte 3 is SBA) is not mistaken for one.
        var record = new HostRecord(RecordFlag.Err, Opcode.PutGet, B(0x10, 0x05, 0x01, 0x11));
        Assert.Empty(Scrub.ScrubProblems(connection.Screen, record));
    }

    [Fact(DisplayName = "recordings of host reads: blank a corrupt restore entirely")]
    public void BlankACorruptRestore()
    {
        var connection = HalfTypedPassword();
        var bogus = B(0x04, 0x12, 0x47, 0x48, 0x09, Ebcdic.Encode(PASSWORD));
        var result = connection.Receive(HostRecord(Opcode.RestoreScreen, bogus));
        Assert.Single(RecordingTestData.Errors(result.Events));
        Assert.False(RecordingTestData.Contains(RecordingTestData.Recorded(connection), PASSWORD));
    }

    [Fact(DisplayName = "recordings of host reads: blank the password in READ SCREEN and READ IMMEDIATE replies")]
    public void BlankThePasswordInHostReads()
    {
        var connection = HalfTypedPassword();
        var screen = connection.Receive(HostRecord(Opcode.ReadScreen, B(0x04, 0x62)));
        var immediate = connection.Receive(HostRecord(Opcode.ReadImmediate, B(0x04, 0x72)));
        Assert.True(RecordingTestData.Contains(screen.Send[0].Bytes, PASSWORD));
        Assert.True(RecordingTestData.Contains(immediate.Send[0].Bytes, PASSWORD));
        var bytes = RecordingTestData.Recorded(connection);
        Assert.False(RecordingTestData.Contains(bytes, PASSWORD));
        Assert.Empty(RecordingTestData.Replay(bytes).Problems);
    }

    [Fact(DisplayName = "recordings of host reads: are caught by the scrub check when not blanked")]
    public void AreCaughtByTheScrubCheck()
    {
        var connection = HalfTypedPassword();
        var save = connection.Receive(HostRecord(Opcode.SaveScreen, B(0x04, 0x02)));
        Assert.Equal(
            ["save reply: nondisplay field at position 532 is not blanked"],
            Scrub.ScrubProblems(connection.Screen, SentRecord(save.Send[0].Bytes)));
    }
}
