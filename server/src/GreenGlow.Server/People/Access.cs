using System.Collections.Concurrent;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.People;

/// <summary>
/// Who may use which connection: administrators may use any; anyone else needs a grant to
/// them or to a group they belong to (directly or through other groups). Group memberships
/// come from the directory and are kept for a few minutes. If the directory can't be reached,
/// the user's groups count as none: access fails closed.
/// </summary>
public sealed class Access(
    IDbContextFactory<GreenGlowDb> dbs,
    IDirectory directory,
    UserStore users,
    TimeProvider clock,
    DemoOptions demo,
    ILogger<Access> log)
{
    public static readonly TimeSpan GroupCacheTime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (IReadOnlySet<string> Groups, DateTimeOffset Until)> _groups = new();

    /// <summary>The enabled connections this user may use, in order.</summary>
    public async Task<IReadOnlyList<Target>> AllowedAsync(CurrentUser user, CancellationToken cancel = default)
    {
        await using var db = await dbs.CreateDbContextAsync(cancel);
        var connections = await db.Connections.AsNoTracking().Include(c => c.Grants).Where(c => c.Enabled)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(cancel);
        // Demo mode: every guest may use every enabled demo system, and nothing else.
        if (demo.Enabled) return connections.Where(c => c.IsDemo).Select(TargetCatalog.ToTarget).ToList();
        if (await IsAdminAsync(user)) return connections.Select(TargetCatalog.ToTarget).ToList();
        var groups = connections.Any(c => c.Grants.Any(g => g.PrincipalType == PrincipalType.Group))
            ? await GroupsAsync(user.Id, cancel)
            : new HashSet<string>();
        return connections.Where(c => Allows(c, user.Id, groups)).Select(TargetCatalog.ToTarget).ToList();
    }

    /// <summary>The connection by name, if it is enabled and this user may use it.</summary>
    public async Task<Target?> FindAsync(CurrentUser user, string name, CancellationToken cancel = default) =>
        (await AllowedAsync(user, cancel)).FirstOrDefault(t => t.Name == name);

    public static bool Allows(HostConnection connection, string userId, IReadOnlySet<string> groups) =>
        connection.Grants.Any(g =>
            g.PrincipalType == PrincipalType.User ? g.PrincipalId == userId : groups.Contains(g.PrincipalId));

    /// <summary>Forgets a user's cached groups, for example when they sign in again.</summary>
    public void Forget(string userId) => _groups.TryRemove(userId, out _);

    private async Task<bool> IsAdminAsync(CurrentUser user) => (await users.FindAsync(user.Id))?.IsAdmin == true;

    public async Task<IReadOnlySet<string>> GroupsAsync(string userId, CancellationToken cancel = default)
    {
        var now = clock.GetUtcNow();
        if (_groups.TryGetValue(userId, out var cached) && cached.Until > now) return cached.Groups;
        try
        {
            var groups = await directory.MemberGroupsAsync(userId, cancel);
            _groups[userId] = (groups, now + GroupCacheTime);
            return groups;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("group lookup failed for {User}: {Error}; treating as no groups", userId, e.GetType().Name);
            return new HashSet<string>();
        }
    }
}
