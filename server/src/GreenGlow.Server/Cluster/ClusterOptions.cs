using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// Several servers: this node's number, the other nodes' peer URLs and the
/// secret that seals the link between them. No peers: a cluster of one.
/// </summary>
public sealed class ClusterOptions
{
    public const int MinSecretLength = 32;

    /// <summary>1 to 9; the digit after GG in this node's device names.</summary>
    public int NodeId { get; set; } = 1;

    /// <summary>Every other node's peer URL by its number: Cluster__Peers__2=http://host2:8081.</summary>
    public Dictionary<string, string> Peers { get; set; } = [];

    /// <summary>Shared by every node; seals the peer link.</summary>
    public string? Secret { get; set; }

    /// <summary>When set, /cluster answers only on this port, and nothing else does.</summary>
    public int? Port { get; set; }

    /// <summary>When set, HAProxy's agent check reads "ready" or "drain" on this TCP port.</summary>
    public int? AgentPort { get; set; }

    /// <summary>How often each peer's journal is pulled when no poke came.</summary>
    public int PullSeconds { get; set; } = 10;

    /// <summary>How often each peer's connection log rows are pulled.</summary>
    public int LogPullSeconds { get; set; } = 60;

    /// <summary>The peers by number, checked.</summary>
    public IReadOnlyDictionary<int, Uri> PeerUrls { get; private set; } = new Dictionary<int, Uri>();

    public bool Clustered => PeerUrls.Count > 0;

    /// <summary>Checks the settings and fills PeerUrls. Throws ConfigException with the reason.</summary>
    public ClusterOptions Validate()
    {
        if (NodeId is < 1 or > 9) throw new ConfigException($"Cluster:NodeId must be from 1 to 9 (it is {NodeId})");
        var peers = new Dictionary<int, Uri>();
        foreach (var (key, value) in Peers)
        {
            if (!int.TryParse(key, out var id) || id is < 1 or > 9)
                throw new ConfigException($"Cluster:Peers:{key} must be named by a node number from 1 to 9");
            if (id == NodeId) throw new ConfigException($"Cluster:Peers:{key} is this node's own number");
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.Query != "" || uri.UserInfo != "")
                throw new ConfigException($"Cluster:Peers:{key} must be an http:// or https:// URL such as http://greenglow2:8081");
            peers[id] = uri;
        }
        if (peers.Count > 0 && (Secret?.Length ?? 0) < MinSecretLength)
            throw new ConfigException($"Cluster:Secret must be at least {MinSecretLength} characters when Cluster:Peers is set");
        foreach (var (name, port) in new[] { ("Port", Port), ("AgentPort", AgentPort) })
            if (port is < 1 or > 65535) throw new ConfigException($"Cluster:{name} must be a TCP port");
        if (Port is not null && Port == AgentPort) throw new ConfigException("Cluster:Port and Cluster:AgentPort must differ");
        if (PullSeconds < 1 || LogPullSeconds < 1) throw new ConfigException("Cluster:PullSeconds and Cluster:LogPullSeconds must be at least 1");
        PeerUrls = peers;
        return this;
    }
}

/// <summary>This node: its number, whether it is draining, and what it runs.</summary>
public sealed class ClusterNode(ClusterOptions options, TimeProvider clock)
{
    private volatile bool _draining;

    public int Id => options.NodeId;

    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    /// <summary>A draining node opens no new sessions; its sessions carry on until they end.</summary>
    public bool Draining => _draining;

    /// <summary>Raised when draining starts or stops.</summary>
    public event Action<bool>? DrainingChanged;

    public void SetDraining(bool draining)
    {
        if (_draining == draining) return;
        _draining = draining;
        DrainingChanged?.Invoke(draining);
    }

    /// <summary>The server's version, as the build stamped it (version+commit).</summary>
    public static string AppVersion { get; } =
        typeof(ClusterNode).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
}
