// Replays real recordings from test/recordings/ (gitignored) when there are any.
// They exist only on a developer's machine, so this checks behaviour without
// storing their contents: no snapshots, no printed screens. With no recordings it passes.

using GreenGlow.Engine;

namespace GreenGlow.Engine.Tests;

public class LocalRecordingsTests
{
    /// <summary>The repository's test/recordings directory, found by walking up from the test binaries.</summary>
    private static string? RecordingsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return Path.Combine(dir.FullName, "test", "recordings");
            }
        }
        return null;
    }

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        var dir = RecordingsDirectory();
        if (dir is not null && Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, "*.bin").Order()) data.Add(Path.GetFileName(file));
        }
        // xUnit fails a theory with no data, so an empty directory runs one trivial case.
        if (data.Count == 0) data.Add("");
        return data;
    }

    [Theory(DisplayName = "local recordings: replays without errors")]
    [MemberData(nameof(Files))]
    public void ReplaysWithoutErrors(string name)
    {
        if (name == "") return; // No local recordings.
        var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC" });
        var events = new List<ConnectionEvent>();
        foreach (var chunk in RecordingCodec.Decode(File.ReadAllBytes(Path.Combine(RecordingsDirectory()!, name))))
        {
            if (chunk.Direction == Direction.Host) events.AddRange(connection.Receive(chunk.Bytes).Events);
        }
        events.AddRange(connection.Closed());
        var problems = events.Where(e => e.Type is "protocol-error" or "data-stream-error" or "unsupported").ToList();
        Assert.Empty(problems);
        var startup = events.OfType<ConnectionEvent.Startup>().FirstOrDefault();
        // A session either reached a screen or was refused its device.
        if (startup is { Result.Ok: true }) Assert.Contains(events, e => e.Type == "screen");
        else Assert.NotNull(startup);
    }
}
