using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Data;

/// <summary>The system settings an administrator can change, with their defaults.</summary>
public static class SettingKeys
{
    /// <summary>Most browsers attached to one host session at once (the owner's included).</summary>
    public const string MaxFrontendsPerSession = "Sessions.MaxFrontends";

    /// <summary>Minutes a host session stays open with no browser attached.</summary>
    public const string IdleMinutes = "Sessions.IdleMinutes";

    /// <summary>Most host sessions open at once, across all users.</summary>
    public const string MaxConnections = "Sessions.MaxConnections";
}

/// <summary>Defaults for settings, from configuration (Sessions__MaxFrontends and so on).</summary>
public sealed class SessionDefaults
{
    public int MaxFrontends { get; set; } = 2;
    public int IdleMinutes { get; set; } = 5;
    public int MaxConnections { get; set; } = 50;
}

/// <summary>
/// Settings: configuration gives the defaults, and a value an administrator saved in the
/// database wins over them. Values are cached; writes go through here.
/// </summary>
public sealed class SettingsStore(IDbContextFactory<GreenGlowDb> dbs, SessionDefaults defaults)
{
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Ranges = new Dictionary<string, (int, int)>
    {
        [SettingKeys.MaxFrontendsPerSession] = (1, 20),
        [SettingKeys.IdleMinutes] = (0, 24 * 60),
        [SettingKeys.MaxConnections] = (1, 10_000),
    };

    private Dictionary<string, string>? _saved;
    private readonly Lock _lock = new();

    public int MaxFrontendsPerSession => Int(SettingKeys.MaxFrontendsPerSession, defaults.MaxFrontends);
    public int IdleMinutes => Int(SettingKeys.IdleMinutes, defaults.IdleMinutes);
    public int MaxConnections => Int(SettingKeys.MaxConnections, defaults.MaxConnections);

    /// <summary>Every setting with its current value and whether it is the configured default.</summary>
    public IReadOnlyList<(string Key, int Value, bool IsDefault)> All()
    {
        var saved = Saved();
        return
        [
            (SettingKeys.MaxFrontendsPerSession, MaxFrontendsPerSession, !saved.ContainsKey(SettingKeys.MaxFrontendsPerSession)),
            (SettingKeys.IdleMinutes, IdleMinutes, !saved.ContainsKey(SettingKeys.IdleMinutes)),
            (SettingKeys.MaxConnections, MaxConnections, !saved.ContainsKey(SettingKeys.MaxConnections)),
        ];
    }

    /// <summary>Saves a value, or clears it back to the default when value is null.</summary>
    public async Task SetAsync(string key, int? value)
    {
        if (!Ranges.TryGetValue(key, out var range)) throw new ArgumentException($"Unknown setting {key}");
        if (value is { } v && (v < range.Min || v > range.Max))
            throw new ArgumentOutOfRangeException(key, $"{key} must be from {range.Min} to {range.Max}");
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Settings.FindAsync(key);
        if (value is null)
        {
            if (row is not null) db.Settings.Remove(row);
        }
        else if (row is null) db.Settings.Add(new Setting { Key = key, Value = value.Value.ToString() });
        else row.Value = value.Value.ToString();
        await db.SaveChangesAsync();
        lock (_lock) _saved = null;
    }

    /// <summary>Forgets the cached values, when another node's changes were applied.</summary>
    public void Forget()
    {
        lock (_lock) _saved = null;
    }

    private int Int(string key, int fallback) =>
        Saved().TryGetValue(key, out var text) && int.TryParse(text, out var value) ? value : fallback;

    private Dictionary<string, string> Saved()
    {
        lock (_lock)
        {
            if (_saved is not null) return _saved;
        }
        using var db = dbs.CreateDbContext();
        var saved = db.Settings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
        lock (_lock) return _saved = saved;
    }
}
