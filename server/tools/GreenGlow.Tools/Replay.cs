// Replays a recording through the engine offline, prints each screen, and checks that no
// client reply or restored screen kept typed nondisplay input.

using System.Text.Encodings.Web;
using System.Text.Json;
using GreenGlow.Engine;

namespace GreenGlow.Tools;

public static class Replay
{
    /// <summary>For printing: camelCase like the server, without escaping non-ASCII text.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string file)
    {
        List<RecordedChunk> chunks;
        try
        {
            chunks = RecordingCodec.Decode(File.ReadAllBytes(file));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot read {file}: {e.Message}");
            return 1;
        }

        var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC" });
        var client = new TelnetDecoder();
        var problems = new List<string>();
        void Problem(long ms, string problem)
        {
            Console.WriteLine($"SCRUB PROBLEM at {ms} ms: {problem}");
            problems.Add(problem);
        }

        foreach (var chunk in chunks)
        {
            if (chunk.Direction == Direction.Client)
            {
                foreach (var e in client.Push(chunk.Bytes))
                {
                    if (e is not TelnetEvent.Record record) continue;
                    if (Records.Parse(record.Data) is not ParsedRecord.Data { Record: var parsed }) continue;
                    Console.WriteLine(
                        $"client record at {chunk.Ms} ms: opcode 0x{parsed.Opcode:x2}, flags 0x{parsed.Flags:x2}, {parsed.Data.Length} data bytes");
                    foreach (var problem in Scrub.ScrubProblems(connection.Screen, parsed)) Problem(chunk.Ms, problem);
                }
                continue;
            }
            foreach (var e in connection.Receive(chunk.Bytes).Events)
            {
                switch (e)
                {
                    case ConnectionEvent.Screen screen:
                        Print(screen.Snapshot);
                        break;
                    case ConnectionEvent.Record record:
                        foreach (var problem in Scrub.RestoreProblems(record.HostRecord)) Problem(chunk.Ms, problem);
                        break;
                    default:
                        Console.WriteLine(JsonSerializer.Serialize(e, e.GetType(), Json));
                        break;
                }
            }
        }
        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"{problems.Count} scrub problem(s): this recording must not be kept.");
            return 1;
        }
        Console.WriteLine("Scrub check: no typed nondisplay input found.");
        return 0;
    }

    private static void Print(ScreenSnapshot snapshot)
    {
        var rule = new string('─', snapshot.Columns);
        var state = new[]
        {
            $"{snapshot.Rows} x {snapshot.Columns}",
            $"cursor {snapshot.Cursor.Row},{snapshot.Cursor.Column}",
            $"keyboard {Name(snapshot.Keyboard)}",
            snapshot.Error is { } error ? $"{Name(error.Kind)} error {error.Code}" : "",
            $"error row {snapshot.ErrorRow}",
            snapshot.PendingRead is { } read ? $"pending read 0x{read:x2}" : "",
        };
        Console.WriteLine(string.Join(", ", state.Where(s => s.Length > 0)));
        Console.WriteLine($"┌{rule}┐");
        foreach (var line in Display.ScreenText(snapshot)) Console.WriteLine($"│{line}│");
        Console.WriteLine($"└{rule}┘");
        if (snapshot.MessageLine is { } messageLine)
        {
            var text = Ebcdic.Decode(messageLine.Select(b => b < 0x40 ? (byte)0x40 : b).ToArray());
            Console.WriteLine($" message line: {JsonSerializer.Serialize(text.TrimEnd(), Json)}");
        }
        foreach (var field in snapshot.Fields)
        {
            var f = field.Format;
            var flags = new (string Name, bool On)[]
            {
                ("bypass", f.Bypass), ("dupEnable", f.DupEnable), ("modified", f.Modified), ("autoEnter", f.AutoEnter),
                ("fieldExitRequired", f.FieldExitRequired), ("monocase", f.Monocase), ("mandatoryEnter", f.MandatoryEnter),
            }.Where(p => p.On).Select(p => p.Name);
            var value = Display.IsNondisplay(field.Attribute)
                ? "(hidden)"
                : JsonSerializer.Serialize(Display.FieldText(snapshot, field), Json);
            Console.WriteLine(
                $"  field {field.Row},{field.Column} length {field.Length} {f.Shift} attribute 0x{field.Attribute:x} {string.Join(" ", flags)} {value}");
        }
    }

    /// <summary>An enum as its JSON string (keyboard state, error kind).</summary>
    private static string Name<T>(T value) => JsonSerializer.Serialize(value, Json).Trim('"');
}

/// <summary>One line of JSON per screen of a recording: the snapshot as the server sends it.</summary>
public static class Snapshots
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static int Run(string file)
    {
        try
        {
            var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC" });
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };
            foreach (var chunk in RecordingCodec.Decode(File.ReadAllBytes(file)))
            {
                if (chunk.Direction != Direction.Host) continue;
                foreach (var e in connection.Receive(chunk.Bytes).Events)
                    if (e is ConnectionEvent.Screen screen) output.WriteLine(JsonSerializer.Serialize(screen.Snapshot, Web));
            }
            output.Flush();
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot replay {file}: {e.Message}");
            return 1;
        }
    }
}
