using System.Collections.Concurrent;
using GreenGlow.Server.Cluster;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>
/// Nodes of one cluster, each an in-process server with its own database, reaching each other's
/// peer API through the test servers. A node is down until a test starts it, and can be taken
/// down again; a call to a node that is down fails as an unreachable host would.
/// </summary>
public sealed class TestCluster : IAsyncDisposable
{
    public const string Admin = "boss@example.com";
    public const string Secret = "a test secret that is long enough to seal with";

    private readonly ConcurrentDictionary<int, RelayFactory> _nodes = new();
    private readonly ConcurrentDictionary<int, bool> _up = new();

    /// <param name="secrets">A node's own secret, where it differs from the others'.</param>
    public TestCluster(int count = 2, string? seed = null, IReadOnlyDictionary<int, string>? secrets = null)
    {
        for (var id = 1; id <= count; id++)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Server:PublicUrl"] = T.Origin,
                ["Entra:InitialAdminUpn"] = Admin,
                ["Seed:ConnectionsJson"] = seed ?? RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
                ["Cluster:NodeId"] = id.ToString(),
                ["Cluster:Secret"] = secrets?.GetValueOrDefault(id) ?? Secret,
                // Pulls happen when a test asks, or when poked; not on a timer underfoot.
                ["Cluster:PullSeconds"] = "3600",
                ["Cluster:LogPullSeconds"] = "3600",
            };
            for (var peer = 1; peer <= count; peer++)
                if (peer != id) settings[$"Cluster:Peers:{peer}"] = $"http://node{peer}.test";
            var factory = new RelayFactory(settings)
            {
                ExtraServices = s => s.AddSingleton<PeerTransport>(new Transport(this)),
            };
            _nodes[id] = factory;
        }
    }

    public RelayFactory this[int id] => _nodes[id];

    /// <summary>Starts a node (its start merges the snapshots of the nodes already up).</summary>
    public RelayFactory Start(int id)
    {
        _ = _nodes[id].Server;
        _up[id] = true;
        return _nodes[id];
    }

    public void Down(int id) => _up[id] = false;

    public void Up(int id) => _up[id] = true;

    public SyncService Sync(int id) => _nodes[id].Services.GetRequiredService<SyncService>();

    /// <summary>Every node pulls from every other, twice, so changes passed on arrive too.</summary>
    public async Task SettleAsync()
    {
        for (var round = 0; round < 2; round++)
            foreach (var id in _up.Where(n => n.Value).Select(n => n.Key).Order())
                await Sync(id).PullNowAsync();
    }

    public GreenGlowDb Db(int id) => _nodes[id].Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();

    public Task<HttpClient> AdminAsync(int id) => _nodes[id].SignedInClientAsync(Admin);

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes.Values) await node.DisposeAsync();
    }

    private sealed class Transport(TestCluster cluster) : PeerTransport
    {
        public override HttpMessageHandler HandlerFor(int node) => new Forward(cluster, node);
    }

    private sealed class Forward(TestCluster cluster, int node) : HttpMessageHandler
    {
        private HttpMessageInvoker? _invoker;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!cluster._up.GetValueOrDefault(node)) throw new HttpRequestException($"node {node} is down");
            _invoker ??= new HttpMessageInvoker(cluster._nodes[node].Server.CreateHandler());
            return _invoker.SendAsync(request, cancellationToken);
        }
    }
}
