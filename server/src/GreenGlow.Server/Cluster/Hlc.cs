using System.Globalization;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// A hybrid logical clock: versions for synced rows that order like wall-clock time but never go
/// backwards, and never repeat across nodes. A version is fixed-width lowercase hex (12 digits of
/// milliseconds, 4 of counter, 2 of node), so comparing the strings orders them.
/// </summary>
public sealed class Hlc(int node, TimeProvider clock)
{
    public const int Length = 18;

    /// <summary>Older than any real version: rows written before versions existed.</summary>
    public static readonly string Zero = new('0', Length);

    private readonly Lock _lock = new();
    private long _millis;
    private int _counter;

    /// <summary>A new version, later than every one made or seen here.</summary>
    public string Next()
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (now > _millis) (_millis, _counter) = (now, 0);
            else if (++_counter > 0xFFFF) (_millis, _counter) = (_millis + 1, 0);
            return Format(_millis, _counter, node);
        }
    }

    /// <summary>Moves the clock past a version seen from another node.</summary>
    public void Witness(string version)
    {
        if (!TryParse(version, out var millis, out var counter)) return;
        lock (_lock)
        {
            if (millis > _millis || (millis == _millis && counter > _counter)) (_millis, _counter) = (millis, counter);
        }
    }

    public static int Compare(string a, string b) => string.CompareOrdinal(a, b);

    public static bool IsValid(string? version) => version is { Length: Length } && TryParse(version, out _, out _);

    public static string Format(long millis, int counter, int node) =>
        $"{millis:x12}{counter:x4}{node:x2}";

    private static bool TryParse(string version, out long millis, out int counter)
    {
        millis = 0;
        counter = 0;
        return version.Length == Length
               && version.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
               && long.TryParse(version.AsSpan(0, 12), NumberStyles.HexNumber, null, out millis)
               && int.TryParse(version.AsSpan(12, 4), NumberStyles.HexNumber, null, out counter);
    }
}
