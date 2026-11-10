using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.People;

public sealed class DevOptions
{
    /// <summary>
    /// Development and tests only: groups the stand-in directory knows, as
    /// "name=member upn;member upn,...". Their object IDs are made from the names. A member of
    /// "*" puts everyone in the group (for tests and npm run dev).
    /// </summary>
    public string Groups { get; set; } = "";
}

/// <summary>
/// The stand-in directory for the dev login, when there is no Entra ID: users are the ones in
/// the database, or any UPN (with the same made-up object ID the dev login gives it), and
/// groups come from Dev:Groups.
/// </summary>
public sealed class LocalDirectory(IDbContextFactory<GreenGlowDb> dbs, DevOptions options) : IDirectory
{
    private readonly IReadOnlyList<(DirectoryEntry Group, HashSet<string> Members)> _groups = ParseGroups(options.Groups);

    public static string GroupId(string name) => AuthSetup.DevObjectId("group:" + name);

    public async Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default)
    {
        upn = upn.Trim();
        if (!upn.Contains('@')) return null;
        await using var db = await dbs.CreateDbContextAsync(cancel);
        var known = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Upn == upn, cancel);
        return known is not null
            ? new DirectoryEntry(known.Id, known.DisplayName, known.Upn)
            : new DirectoryEntry(AuthSetup.DevObjectId(upn), upn.Split('@')[0], upn);
    }

    public async Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        await using var db = await dbs.CreateDbContextAsync(cancel);
        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var users = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.Like(u.Upn, pattern) || EF.Functions.Like(u.DisplayName, pattern))
            .OrderBy(u => u.DisplayName).Take(15).ToListAsync(cancel);
        var found = users.Select(u => new DirectoryEntry(u.Id, u.DisplayName, u.Upn)).ToList();
        if (query.Contains('@') && found.All(u => !string.Equals(u.Upn, query, StringComparison.OrdinalIgnoreCase)))
            found.Insert(0, (await FindUserAsync(query, cancel))!);
        return found;
    }

    public Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyList<DirectoryEntry>>(_groups
            .Where(g => g.Group.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Group).ToList());

    public Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyList<DirectoryEntry>>(_groups
            .Where(g => string.Equals(g.Group.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Group).ToList());

    public async Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default)
    {
        await using var db = await dbs.CreateDbContextAsync(cancel);
        var upn = await db.Users.Where(u => u.Id == userId).Select(u => u.Upn).FirstOrDefaultAsync(cancel);
        return upn is null
            ? new HashSet<string>()
            : _groups.Where(g => g.Members.Contains(upn) || g.Members.Contains("*")).Select(g => g.Group.Id).ToHashSet();
    }

    private static List<(DirectoryEntry, HashSet<string>)> ParseGroups(string text) =>
        text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2))
            .Select(p => (new DirectoryEntry(GroupId(p[0].Trim()), p[0].Trim(), null),
                (p.Length > 1 ? p[1] : "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
}
