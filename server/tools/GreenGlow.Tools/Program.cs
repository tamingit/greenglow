// Dev-only tools (never in the container):
//
//   npm run capture -- <target> [--collision | --reconnect] [--url tcp://host:port] [--db <path>]
//       records a live session on a test IBM i up to its first screen (Capture.cs)
//   npm run replay -- test/recordings/<file>.bin
//       prints each screen of a recording and checks it holds no typed nondisplay input (Replay.cs)
//   dotnet run --project server/tools/GreenGlow.Tools -- snapshots <file>.bin
//       one JSON snapshot per screen on stdout, as the server sends them (for web tests)

using System.Text;
using GreenGlow.Tools;

Console.OutputEncoding = Encoding.UTF8;

return args switch
{
    ["capture", .. var rest] => await Capture.RunAsync(rest),
    ["replay", var file] => Replay.Run(file),
    ["snapshots", var file] => Snapshots.Run(file),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          capture <target> [--collision | --reconnect] [--url tcp://host:port] [--db <path>]
          replay <recording.bin>
          snapshots <recording.bin>
        """);
    return 1;
}
