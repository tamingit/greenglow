namespace GreenGlow.Server.Cluster;

/// <summary>
/// The newest version this node has of one synced row, and whether that version deleted it (a
/// tombstone, kept so an older copy of the row never comes back).
/// </summary>
public sealed class SyncVersion
{
    public required string Table { get; set; }
    public required string Key { get; set; }
    public required string Version { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>
/// One entry of this node's change journal: a synced row as written (here, or applied from a
/// peer and passed on), or null data for a delete. Peers pull it in Seq order.
/// </summary>
public sealed class SyncChange
{
    public long Seq { get; set; }
    public required string Table { get; set; }
    public required string Key { get; set; }
    public required string Version { get; set; }

    /// <summary>The row's columns as JSON, or null when the row was deleted.</summary>
    public string? Data { get; set; }

    /// <summary>When it was journalled (Unix milliseconds), for pruning.</summary>
    public long At { get; set; }
}

/// <summary>A connection log row was written or updated here; peers pull the rows in Seq order.</summary>
public sealed class LogChange
{
    public long Seq { get; set; }
    public Guid LogId { get; set; }

    /// <summary>Unix milliseconds, for pruning.</summary>
    public long At { get; set; }
}

/// <summary>How far this node has pulled from a peer: its journal (by that journal's id) and its log rows.</summary>
public sealed class PeerCursor
{
    public int Node { get; set; }
    public string JournalId { get; set; } = "";
    public long Changes { get; set; }

    /// <summary>The peer's journal id when Logs was read: a new one starts its log rows over.</summary>
    public string LogsJournalId { get; set; } = "";
    public long Logs { get; set; }
}

/// <summary>This node's own facts, never synced, such as its journal's id.</summary>
public sealed class LocalState
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
