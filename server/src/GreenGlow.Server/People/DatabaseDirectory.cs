using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using GreenGlow.Server.HostServers;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.People;

/// <summary>
/// The directory when people sign in with local accounts or IBM i user profiles: the users
/// are the ones in the database, and there are no groups but the built-in Everyone (which
/// CurrentDirectory adds). With IBM i sign-in a user profile nobody has signed in with yet can
/// still be named (to give it access, or make it an administrator, ahead of its first sign-in).
/// </summary>
public sealed class DatabaseDirectory(IDbContextFactory<GreenGlowDb> dbs, SignInConfig config) : IDirectory
{
    private UserSource Source => config.Mode == SignInMode.IbmI ? UserSource.IbmI : UserSource.Local;

    public async Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default)
    {
        upn = upn.Trim();
        var source = Source;
        await using var db = await dbs.CreateDbContextAsync(cancel);
        var known = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Upn == upn && u.Source == source, cancel);
        if (known is not null) return new DirectoryEntry(known.Id, known.DisplayName, known.Upn);
        var profile = upn.ToUpperInvariant();
        return source == UserSource.IbmI && HostServerClient.UserPattern().IsMatch(profile)
            ? new DirectoryEntry(UserStore.IbmUserId(profile), profile, profile)
            : null;
    }

    public async Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var source = Source;
        await using var db = await dbs.CreateDbContextAsync(cancel);
        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Source == source && !u.Disabled
                        && (EF.Functions.Like(u.Upn, pattern) || EF.Functions.Like(u.DisplayName, pattern)))
            .OrderBy(u => u.DisplayName).Take(15).ToListAsync(cancel);
        var found = users.Select(u => new DirectoryEntry(u.Id, u.DisplayName, u.Upn)).ToList();
        // An IBM i user profile typed in full can be picked before it has ever signed in.
        if (found.All(u => !string.Equals(u.Upn, query, StringComparison.OrdinalIgnoreCase))
            && source == UserSource.IbmI && await FindUserAsync(query, cancel) is { } typed)
            found.Insert(0, typed);
        return found;
    }

    public Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyList<DirectoryEntry>>([]);

    public Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyList<DirectoryEntry>>([]);

    public Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}

/// <summary>
/// The directory for the sign-in method in force: Microsoft Graph with Entra ID, the stand-in
/// with the dev login, and the database's own users otherwise. Each has the built-in group
/// Everyone too: searches offer it, and its name always means it (Access makes every signed-in
/// user a member).
/// </summary>
public sealed class CurrentDirectory(SignInConfig config, IServiceProvider services) : IDirectory
{
    /// <summary>The key of the one Graph directory, which keeps its token between calls.</summary>
    public const string Graph = "graph";

    private IDirectory Inner => config.Mode switch
    {
        SignInMode.Entra => services.GetRequiredKeyedService<IDirectory>(Graph),
        SignInMode.Dev => services.GetRequiredService<LocalDirectory>(),
        _ => services.GetRequiredService<DatabaseDirectory>(),
    };

    public Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default) => Inner.FindUserAsync(upn, cancel);

    public Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default) =>
        Inner.SearchUsersAsync(query, cancel);

    public async Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default)
    {
        var found = await Inner.SearchGroupsAsync(query, cancel);
        return Everyone.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            ? [Everyone.Entry, .. found.Where(g => g.Id != Everyone.Id)]
            : found;
    }

    public async Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default) =>
        Everyone.IsName(name) ? [Everyone.Entry] : await Inner.FindGroupsByNameAsync(name, cancel);

    public Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default) =>
        Inner.MemberGroupsAsync(userId, cancel);
}
