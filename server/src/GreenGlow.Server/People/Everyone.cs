namespace GreenGlow.Server.People;

/// <summary>
/// The built-in group Everyone: every signed-in user, whatever the sign-in method. Access given
/// to it opens a connection (or a macro) to all of them. Membership never comes from the
/// directory, so it holds while Entra ID can't be reached. Its name is reserved: an Entra ID
/// group that is also called Everyone must be given by its object ID.
/// </summary>
public static class Everyone
{
    public const string Name = "Everyone";

    /// <summary>The group's ID: the same on every node, and the one the stand-in directory gives "Everyone".</summary>
    public static readonly string Id = LocalDirectory.GroupId(Name);

    public static readonly DirectoryEntry Entry = new(Id, Name, null);

    public static bool IsName(string? name) => string.Equals(name?.Trim(), Name, StringComparison.OrdinalIgnoreCase);
}
