namespace GreenGlow.Server.Configuration;

/// <summary>
/// Development only: loads KEY=VALUE lines from the repository's .env (gitignored) into the
/// configuration, under anything already set in the real environment.
/// </summary>
public static class DotEnv
{
    public static Dictionary<string, string?> Read(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            if (Environment.GetEnvironmentVariable(key) is null) values[key.Replace("__", ":")] = value;
        }
        return values;
    }

    /// <summary>The .env in the nearest parent directory holding a .git folder.</summary>
    public static string? Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return Path.Combine(dir.FullName, ".env");
        return null;
    }
}
