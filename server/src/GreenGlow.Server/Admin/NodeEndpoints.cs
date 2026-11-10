using GreenGlow.Server.Auth;
using GreenGlow.Server.Cluster;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/nodes: every node of the cluster with its sessions, and Drain, Resume and Resync
/// from scratch for any of them. Another node is asked over the peer link.
/// </summary>
public static class NodeEndpoints
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResyncTimeout = TimeSpan.FromMinutes(3);

    public sealed record DrainBody(bool Drain);

    public static void MapNodeEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/nodes", async (HttpContext context, ClusterOptions options, PeerClient peers, SyncService sync) =>
        {
            var self = ClusterEndpoints.Status(context.RequestServices);
            var others = await Task.WhenAll(options.PeerUrls.Keys.Order().Select(async id =>
            {
                var state = sync.Peers.GetValueOrDefault(id);
                var syncInfo = new { lastSync = state?.LastSync, lastError = state?.LastError, lastErrorAt = state?.LastErrorAt };
                try
                {
                    var status = await peers.CallAsync<NodeStatus>(id, "status", new { }, StatusTimeout, context.RequestAborted);
                    return Describe(status, self: false, error: null, syncInfo);
                }
                catch (PeerException e)
                {
                    return new
                    {
                        id,
                        self = false,
                        state = "unreachable",
                        error = (string?)e.Message,
                        version = (string?)null,
                        protocol = (int?)null,
                        oldestProtocol = (int?)null,
                        startedAt = (DateTimeOffset?)null,
                        sessions = (IReadOnlyList<SessionSummary>)[],
                        sync = (object?)syncInfo,
                    };
                }
            }));
            return Results.Json(new
            {
                node = self.Node,
                clustered = options.Clustered,
                nodes = (object[])[Describe(self, self: true, error: null, sync: null), .. others],
            });
        });

        admin.MapPost("/nodes/{id:int}/drain", async (int id, DrainBody body, HttpContext context, ClusterOptions options,
            PeerClient peers) =>
        {
            var by = CurrentUser.From(context.User)!.Upn;
            if (id == options.NodeId)
            {
                await NodeActions.DrainAsync(context.RequestServices, body.Drain, by, null);
                return Results.NoContent();
            }
            if (!options.PeerUrls.ContainsKey(id)) return Results.NotFound();
            try
            {
                await peers.CallAsync<NodeStatus>(id, "drain", new DrainRequest(body.Drain, by), ActionTimeout, context.RequestAborted);
                return Results.NoContent();
            }
            catch (PeerException e)
            {
                return Unreachable(id, e);
            }
        });

        admin.MapPost("/nodes/{id:int}/resync", async (int id, HttpContext context, ClusterOptions options, PeerClient peers) =>
        {
            var by = CurrentUser.From(context.User)!.Upn;
            ResyncResult result;
            if (id == options.NodeId)
            {
                var (ok, message) = await NodeActions.ResyncAsync(context.RequestServices, by, null, context.RequestAborted);
                result = new ResyncResult(ok, message);
            }
            else if (!options.PeerUrls.ContainsKey(id)) return Results.NotFound();
            else
            {
                try
                {
                    result = await peers.CallAsync<ResyncResult>(id, "resync", new ResyncRequest(by), ResyncTimeout, context.RequestAborted);
                }
                catch (PeerException e)
                {
                    return Unreachable(id, e);
                }
            }
            return result.Ok
                ? Results.Json(new { message = result.Message })
                : Results.Conflict(new { error = "resync", message = result.Message });
        });
    }

    private static object Describe(NodeStatus status, bool self, string? error, object? sync) => new
    {
        id = status.Node,
        self,
        state = status.Draining ? "draining" : "active",
        error,
        version = (string?)status.Version,
        protocol = (int?)status.Protocol,
        oldestProtocol = (int?)status.OldestProtocol,
        startedAt = (DateTimeOffset?)status.StartedAt,
        sessions = status.Sessions,
        sync,
    };

    private static IResult Unreachable(int id, PeerException e) =>
        Results.Json(new { error = "node", message = $"Node {id} couldn't be asked: {e.Message}" },
            statusCode: StatusCodes.Status502BadGateway);
}
