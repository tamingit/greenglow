namespace GreenGlow.Tools;

/// <summary>
/// The repository root and its .env (gitignored): KEY=VALUE lines, under anything already
/// set in the real environment.
/// </summary>
public static class Env
{
    private static readonly Lazy<Dictionary<string, string>> DotEnv = new(ReadDotEnv);

    /// <summary>The nearest directory holding .git, from the working directory or the binaries.</summary>
    public static string RepoRoot { get; } = FindRoot(Directory.GetCurrentDirectory())
                                             ?? FindRoot(AppContext.BaseDirectory)
                                             ?? Directory.GetCurrentDirectory();

    public static string? Get(string key) =>
        Environment.GetEnvironmentVariable(key) is { } value ? value : DotEnv.Value.GetValueOrDefault(key);

    /// <summary>A path from the environment or .env, relative to the repository root.</summary>
    public static string FromRoot(string path) => Path.GetFullPath(Path.Combine(RepoRoot, path));

    private static string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        return null;
    }

    private static Dictionary<string, string> ReadDotEnv()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(RepoRoot, ".env");
        if (!File.Exists(path)) return values;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            values[line[..eq].Trim()] = value;
        }
        return values;
    }
}
