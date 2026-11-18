using System.Collections.Concurrent;
using GreenGlow.Server.Auth;

namespace GreenGlow.Server.Logins;

/// <summary>
/// IBM i passwords held in this server's memory for signed-in users, so the terminal and the
/// printer panel can sign on without asking again: the one typed at IBM i sign-in, and the
/// ones typed for saved logins. Nothing here is written anywhere. An entry goes when its user
/// signs out, when it hasn't been used for as long as a sign-in lasts (Auth:SessionHours), and
/// when the server stops; another node never has it, and asks again.
/// </summary>
public sealed class CredentialVault(TimeProvider clock, AuthOptions auth)
{
    private readonly ConcurrentDictionary<(string User, string Connection, string IbmUser), (string Password, long Used)> _held = new();

    private TimeSpan Life => TimeSpan.FromHours(Math.Max(1, auth.SessionHours));

    public void Put(string userId, string connection, string ibmUser, string password)
    {
        _held[(userId, connection, ibmUser.ToUpperInvariant())] = (password, clock.GetUtcNow().UtcTicks);
        Sweep();
    }

    /// <summary>The held password, if any; getting it counts as using it.</summary>
    public string? Get(string userId, string connection, string ibmUser)
    {
        var key = (userId, connection, ibmUser.ToUpperInvariant());
        if (!_held.TryGetValue(key, out var entry)) return null;
        var now = clock.GetUtcNow();
        if (now.UtcTicks - entry.Used > Life.Ticks)
        {
            _held.TryRemove(key, out _);
            return null;
        }
        _held[key] = (entry.Password, now.UtcTicks);
        return entry.Password;
    }

    public bool Has(string userId, string connection, string ibmUser) =>
        _held.TryGetValue((userId, connection, ibmUser.ToUpperInvariant()), out var entry)
        && clock.GetUtcNow().UtcTicks - entry.Used <= Life.Ticks;

    /// <summary>Drops one password: it was refused, or its login was removed.</summary>
    public void Forget(string userId, string connection, string ibmUser) =>
        _held.TryRemove((userId, connection, ibmUser.ToUpperInvariant()), out _);

    /// <summary>Drops everything held for a user: they signed out.</summary>
    public void Forget(string userId)
    {
        foreach (var key in _held.Keys.Where(k => k.User == userId)) _held.TryRemove(key, out _);
    }

    private void Sweep()
    {
        var edge = clock.GetUtcNow().UtcTicks - Life.Ticks;
        foreach (var entry in _held.Where(e => e.Value.Used < edge)) _held.TryRemove(entry);
    }
}
