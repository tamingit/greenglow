using System.Collections.Concurrent;

namespace GreenGlow.Server.Sessions;

/// <summary>
/// Who has GreenGlow open now, for the owner's sharing panel: a user is in while one of their
/// pages holds /ws/user (UserFeed), or asked for their sessions within <see cref="Window"/>.
/// Kept in memory only; nothing is stored.
/// </summary>
public sealed class Presence(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.OrdinalIgnoreCase);

    public void Seen(string upn) => _seen[upn] = clock.GetUtcNow();

    /// <summary>A page of this user's opened its /ws/user socket.</summary>
    public void Enter(string upn) => _open.AddOrUpdate(upn, 1, (_, n) => n + 1);

    public void Leave(string upn) => _open.AddOrUpdate(upn, 0, (_, n) => Math.Max(0, n - 1));

    public bool IsIn(string upn) =>
        (_open.TryGetValue(upn, out var n) && n > 0)
        || (_seen.TryGetValue(upn, out var at) && clock.GetUtcNow() - at < Window);
}
