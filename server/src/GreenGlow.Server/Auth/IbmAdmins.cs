using GreenGlow.Server.Data;
using GreenGlow.Server.HostServers;

namespace GreenGlow.Server.Auth;

/// <summary>
/// With IBM i sign-in: which IBM i user profiles are GreenGlow administrators, decided by the IBM
/// i itself. A profile is one when its group profile or a supplemental group is one of
/// <see cref="Groups"/>, or it has one of the special authorities in <see cref="Authorities"/>.
/// Kept in the settings (so every node has it), as comma-separated lists. Empty: the IBM i
/// decides nothing, and administrators are only the ones named on the admin page.
/// </summary>
public sealed record IbmAdmins(IReadOnlyList<string> Groups, IReadOnlyList<string> Authorities)
{
    public static readonly IbmAdmins None = new([], []);

    public bool IsSet => Groups.Count + Authorities.Count > 0;

    public bool Admits(IbmProfile profile) =>
        profile.Groups.Any(g => Groups.Contains(g)) || profile.SpecialAuthorities.Any(a => Authorities.Contains(a));

    /// <summary>The rule in force: from the settings, ignoring anything that isn't valid.</summary>
    public static IbmAdmins From(SettingsStore settings) =>
        TryParse(settings.Text(AuthKeys.IbmAdminGroups), settings.Text(AuthKeys.IbmAdminAuthorities), out var rule, out _, out _)
            ? rule
            : None;

    /// <summary>
    /// Group profile names (GGADMIN) and special authorities (*SECADM, or SECADM), comma or space
    /// separated, any case. False, with a message, for a name that can't be a profile or an
    /// authority the IBM i doesn't have; <paramref name="field"/> says which list ("groups" or "authorities").
    /// </summary>
    public static bool TryParse(string? groups, string? authorities, out IbmAdmins rule, out string? field, out string? problem)
    {
        rule = None;
        (field, problem) = (null, null);
        var groupList = Split(groups);
        if (groupList.FirstOrDefault(g => !HostServerClient.UserPattern().IsMatch(g)) is { } badGroup)
        {
            (field, problem) = ("groups", $"\"{badGroup}\" isn't an IBM i group profile name.");
            return false;
        }
        var authorityList = Split(authorities).Select(a => a.StartsWith('*') ? a : "*" + a).ToList();
        if (authorityList.FirstOrDefault(a => !RemoteCommand.SpecialAuthorityNames.Contains(a)) is { } badAuthority)
        {
            (field, problem) = ("authorities",
                $"\"{badAuthority}\" isn't a special authority. Use {string.Join(", ", RemoteCommand.SpecialAuthorityNames)}.");
            return false;
        }
        rule = new IbmAdmins(groupList, authorityList);
        return true;
    }

    /// <summary>The settings to store for this rule (null removes a list).</summary>
    public IReadOnlyDictionary<string, string?> Settings() => new Dictionary<string, string?>
    {
        [AuthKeys.IbmAdminGroups] = Groups.Count > 0 ? string.Join(",", Groups) : null,
        [AuthKeys.IbmAdminAuthorities] = Authorities.Count > 0 ? string.Join(",", Authorities) : null,
    };

    private static List<string> Split(string? text) =>
        (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).Distinct().ToList();
}
