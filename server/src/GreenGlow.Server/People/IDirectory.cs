namespace GreenGlow.Server.People;

/// <summary>A person or group in Entra ID, as the admin and share screens show them.</summary>
public sealed record DirectoryEntry(string Id, string Name, string? Upn);

/// <summary>
/// Entra ID lookups: finding people and groups for the admin and share screens, and the groups a
/// user belongs to for connection access. Microsoft Graph in production; a stand-in without it.
/// </summary>
public interface IDirectory
{
    /// <summary>A user by UPN, or null if there is none.</summary>
    Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default);

    Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default);

    Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default);

    /// <summary>Groups whose display name is exactly this (names aren't unique in Entra ID).</summary>
    Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default);

    /// <summary>The object IDs of every group the user belongs to, directly or through other groups.</summary>
    Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default);
}
