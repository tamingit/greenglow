namespace GreenGlow.Server.Data;

/// <summary>A system setting an administrator can change, stored as text.</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>An Entra ID user who has signed in at least once.</summary>
public sealed class AppUser
{
    /// <summary>The Entra object ID (oid claim).</summary>
    public required string Id { get; set; }
    public required string Upn { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsAdmin { get; set; }

    /// <summary>A demo guest (Demo:Enabled): no account, removed some time after their last visit.</summary>
    public bool IsGuest { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>An IBM i system users may open sessions on.</summary>
public sealed class HostConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Lowercase letters, digits and dashes: what URLs and the API use.</summary>
    public required string Name { get; set; }
    public required string Label { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; }

    /// <summary>tls:// (verified TLS) when true, tcp:// (plain Telnet, test systems only) when false.</summary>
    public bool Secure { get; set; }

    /// <summary>Most host sessions one user may hold open on this system at once.</summary>
    public int MaxSessionsPerUser { get; set; } = 3;

    /// <summary>A test system: automated tests may sign on here; never true for production.</summary>
    public bool IsTest { get; set; }

    /// <summary>A demo system (the fake IBM i, a public IBM i): the only kind demo mode allows.</summary>
    public bool IsDemo { get; set; }

    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>The system's colour (#rrggbb) on the page, or none; users may choose their own.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// For tls://: a CA an administrator trusted for this connection only (PEM), as well as the
    /// server's own (Relay:CaPem). With one, a certificate that doesn't chain to a trusted CA is refused.
    /// </summary>
    public string? TrustedCaPem { get; set; }

    /// <summary>
    /// The name the certificate must have, when an administrator accepted one that doesn't name
    /// the host. Null: it must name the host.
    /// </summary>
    public string? TrustedName { get; set; }

    /// <summary>Who trusted the CA, and when.</summary>
    public string? TrustedBy { get; set; }
    public DateTimeOffset? TrustedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ConnectionGrant> Grants { get; set; } = [];

    public string Url => $"{(Secure ? "tls" : "tcp")}://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
}

/// <summary>A user's own colour for a connection, in place of the one an administrator set.</summary>
public sealed class UserConnectionColor
{
    public required string UserId { get; set; }
    public Guid ConnectionId { get; set; }
    public HostConnection? Connection { get; set; }

    /// <summary>#rrggbb.</summary>
    public required string Color { get; set; }
}

public enum PrincipalType
{
    User,
    Group,
}

/// <summary>
/// Who may use a connection: an Entra user or group. A connection with no grants is open to
/// administrators only.
/// </summary>
public sealed class ConnectionGrant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConnectionId { get; set; }
    public HostConnection? Connection { get; set; }
    public PrincipalType PrincipalType { get; set; }

    /// <summary>The Entra object ID of the user or group.</summary>
    public required string PrincipalId { get; set; }

    /// <summary>The name shown in the admin UI: a UPN or a group name.</summary>
    public string DisplayName { get; set; } = "";
}

public enum LogKind
{
    /// <summary>A host session: the Telnet connection to the IBM i.</summary>
    Session,

    /// <summary>A browser attached to a host session.</summary>
    Frontend,

    /// <summary>An administrator trusted a connection's certificate, or stopped trusting it.</summary>
    Certificate,

    /// <summary>A print session: signed on to the IBM i's Network Print server (printer output).</summary>
    Printer,

    /// <summary>An administrator drained, resumed or resynced a node (several servers).</summary>
    Node,
}

/// <summary>
/// The connection log: who connected, from where, to which system and when. Metadata only,
/// never screen contents or keystrokes. A row with no EndedAt is still open.
/// </summary>
public sealed class ConnectionLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The node that wrote the row; only it changes the row.</summary>
    public int Node { get; set; }

    /// <summary>The row's sync version (hybrid logical clock), set by the database on every write.</summary>
    public string Version { get; set; } = "";

    public LogKind Kind { get; set; }
    public Guid SessionId { get; set; }
    public string? UserId { get; set; }
    public string? Upn { get; set; }
    public string? ClientIp { get; set; }
    public string ConnectionName { get; set; } = "";
    public string? DeviceName { get; set; }

    /// <summary>For frontends: owner, full or view.</summary>
    public string? Role { get; set; }

    /// <summary>For print sessions: the IBM i user signed on to the Network Print server.</summary>
    public string? IbmUser { get; set; }

    /// <summary>For print sessions: how many spooled files were opened. Never which ones.</summary>
    public int? FilesOpened { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
}

public enum ShareAccess
{
    ViewOnly,
    FullAccess,
}

/// <summary>A link to a live host session, shared with named users.</summary>
public sealed class SessionShare
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public required string OwnerUserId { get; set; }

    /// <summary>The random part of the link; only people in Recipients can use it.</summary>
    public required string Token { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public List<ShareRecipient> Recipients { get; set; } = [];
}

public sealed class ShareRecipient
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ShareId { get; set; }
    public SessionShare? Share { get; set; }

    /// <summary>The Entra object ID when known (from a directory search).</summary>
    public string? UserId { get; set; }
    public required string Upn { get; set; }
    public string DisplayName { get; set; } = "";
    public ShareAccess Access { get; set; }
}

/// <summary>
/// A user's macro, kept as Host-on-Demand macro XML (the format ACS saves), so macros move
/// between ACS and GreenGlow unchanged. It holds keys and screen checks only: recording never
/// keeps what was typed into a nondisplay field.
/// </summary>
public sealed class Macro
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string OwnerUserId { get; set; }
    public required string OwnerUpn { get; set; }

    /// <summary>Unique among one owner's macros, ignoring case.</summary>
    public required string Name { get; set; }

    public string Description { get; set; } = "";

    /// <summary>The Host-on-Demand macro XML.</summary>
    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<MacroShare> Shares { get; set; } = [];
}

/// <summary>Someone a macro is shared with: an Entra user or group, who may play it but not change it.</summary>
public sealed class MacroShare
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MacroId { get; set; }
    public Macro? Macro { get; set; }
    public PrincipalType PrincipalType { get; set; }

    /// <summary>The Entra object ID of the user or group.</summary>
    public required string PrincipalId { get; set; }

    /// <summary>A UPN or a group name, as the owner chose it.</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>A key combination a user gave a macro (their own, or one shared with them).</summary>
public sealed class MacroKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string UserId { get; set; }
    public Guid MacroId { get; set; }
    public Macro? Macro { get; set; }

    /// <summary>As the page names it, such as "Alt+Shift+1"; unique per user.</summary>
    public required string Keys { get; set; }
}

/// <summary>A key of the ASP.NET Data Protection key ring (sign-in cookies), synced so every node reads every cookie.</summary>
public sealed class SigningKey
{
    /// <summary>The key's friendly name, such as key-&lt;guid&gt;.</summary>
    public required string Name { get; set; }

    /// <summary>The key's XML as Data Protection writes it.</summary>
    public required string Xml { get; set; }
}
