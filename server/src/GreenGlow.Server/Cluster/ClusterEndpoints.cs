using System.Collections.Concurrent;
using System.Text.Json;
using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;

namespace GreenGlow.Server.Cluster;

/// <summary>One live session, as the admin page's Nodes tab lists it.</summary>
public sealed record SessionSummary(
    Guid Id,
    string Owner,
    string OwnerName,
    string Connection,
    string Label,
    string? Device,
    string? IbmUser,
    string Title,
    DateTimeOffset CreatedAt,
    int Frontends,
    DateTimeOffset? IdleSince,
    bool SignedOn)
{
    public static SessionSummary Of(HostSession s) => new(s.Id, s.Owner.Upn, s.Owner.Name, s.Target.Name, s.Target.Label,
        s.DeviceName, s.IbmUser, s.Title, s.CreatedAt, s.FrontendCount, s.IdleSince, s.SignedOn);
}

/// <summary>A node as it describes itself.</summary>
public sealed record NodeStatus(
    int Node,
    string Version,
    int Protocol,
    int OldestProtocol,
    bool Draining,
    DateTimeOffset StartedAt,
    IReadOnlyList<SessionSummary> Sessions);

public sealed record AfterRequest(long After, int Limit);

public sealed record DrainRequest(bool Drain, string? By);

public sealed record ResyncRequest(string? By);

public sealed record ResyncResult(bool Ok, string Message);

/// <summary>A request's nonce is accepted once, within the clock window it was sealed for.</summary>
public sealed class ReplayGuard(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, long> _seen = new();

    public bool TryUse(string nonce)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (_seen.Count > 10_000)
            foreach (var (key, until) in _seen)
                if (until < now) _seen.TryRemove(key, out _);
        return _seen.TryAdd(nonce, now + 2 * (long)PeerProtocol.ClockSkew.TotalSeconds);
    }
}

/// <summary>
/// The peer API, POST /cluster/{op}, for other nodes only: every request is sealed by a configured
/// peer for this node, within the clock window, and never replayed; every answer is sealed for
/// its request. With Cluster:Port set it answers only on that port, and nothing else does.
/// </summary>
public static class ClusterEndpoints
{
    /// <summary>Snapshots of a full database stay well under this.</summary>
    private const int MaxRequest = 1024 * 1024;

    public static void UseCluster(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ClusterOptions>();
        app.Use(async (context, next) =>
        {
            var isCluster = context.Request.Path.StartsWithSegments("/cluster", out var rest);
            if (options.Port is { } port && (context.Connection.LocalPort == port) != isCluster)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!isCluster)
            {
                await next(context);
                return;
            }
            await HandleAsync(context, options, rest.Value?.Trim('/') ?? "");
        });
    }

    private static async Task HandleAsync(HttpContext context, ClusterOptions options, string op)
    {
        var response = context.Response;
        if (!options.Clustered || !HttpMethods.IsPost(context.Request.Method))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var services = context.RequestServices;
        var clock = services.GetRequiredService<TimeProvider>();
        var seal = new Seal(options.Secret!);
        if (!int.TryParse(context.Request.Headers[PeerProtocol.FromHeader], out var from) || !options.PeerUrls.ContainsKey(from)
            || !long.TryParse(context.Request.Headers[PeerProtocol.TimeHeader], out var time)
            || Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - time) > PeerProtocol.ClockSkew.TotalSeconds)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var body = await ReadAsync(context.Request.Body, context.RequestAborted);
        var plain = body is null ? null : seal.Open(body, Seal.RequestContext(op, from, options.NodeId, time));
        if (plain is null || !services.GetRequiredService<ReplayGuard>().TryUse(Seal.NonceOf(body)))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        PeerAnswer answer;
        try
        {
            var request = JsonSerializer.Deserialize<PeerRequest>(plain, PeerProtocol.Json)!;
            var protocol = Math.Min(request.Protocol, PeerProtocol.Version);
            if (protocol < Math.Max(request.Oldest, PeerProtocol.Oldest))
                answer = new PeerAnswer(PeerProtocol.Version,
                    $"Protocol mismatch: node {options.NodeId} speaks {PeerProtocol.Oldest}–{PeerProtocol.Version}, node {from} {request.Oldest}–{request.Protocol}. Upgrade the older one.",
                    null);
            else
            {
                var result = await DispatchAsync(services, options, from, op, request.Body, context.RequestAborted);
                answer = result is null
                    ? new PeerAnswer(protocol, $"Unknown operation {op}.", null)
                    : new PeerAnswer(protocol, null, JsonSerializer.SerializeToElement(result, PeerProtocol.Json));
            }
        }
        catch (JsonException)
        {
            answer = new PeerAnswer(PeerProtocol.Version, "A request this node couldn't read.", null);
        }
        var sealedAnswer = seal.Close(JsonSerializer.SerializeToUtf8Bytes(answer, PeerProtocol.Json), Seal.ResponseContext(Seal.NonceOf(body!)));
        response.ContentType = "application/octet-stream";
        response.Headers.CacheControl = "no-store";
        await response.Body.WriteAsync(sealedAnswer, context.RequestAborted);
    }

    private static async Task<object?> DispatchAsync(IServiceProvider services, ClusterOptions options, int from, string op,
        JsonElement body, CancellationToken cancel)
    {
        var store = services.GetRequiredService<SyncStore>();
        switch (op)
        {
            case "status":
                return Status(services);
            case "changes":
            {
                var request = body.Deserialize<AfterRequest>(PeerProtocol.Json)!;
                return await store.ChangesAsync(request.After, Math.Clamp(request.Limit, 1, SyncService.ChangesPerPage));
            }
            case "snapshot":
                return await store.SnapshotAsync();
            case "logs":
            {
                var request = body.Deserialize<AfterRequest>(PeerProtocol.Json)!;
                return await store.LogsAsync(request.After, Math.Clamp(request.Limit, 1, SyncService.LogsPerPage));
            }
            case "poke":
                services.GetRequiredService<SyncService>().PullSoon(from);
                return new { };
            case "drain":
            {
                var request = body.Deserialize<DrainRequest>(PeerProtocol.Json)!;
                await NodeActions.DrainAsync(services, request.Drain, request.By, from);
                return Status(services);
            }
            case "resync":
            {
                var request = body.Deserialize<ResyncRequest>(PeerProtocol.Json)!;
                var (ok, message) = await NodeActions.ResyncAsync(services, request.By, from, cancel);
                return new ResyncResult(ok, message);
            }
            default:
                return null;
        }
    }

    /// <summary>This node and its live sessions.</summary>
    public static NodeStatus Status(IServiceProvider services)
    {
        var node = services.GetRequiredService<ClusterNode>();
        var sessions = services.GetRequiredService<SessionManager>().All.OrderBy(s => s.CreatedAt).Select(SessionSummary.Of).ToList();
        return new NodeStatus(node.Id, ClusterNode.AppVersion, PeerProtocol.Version, PeerProtocol.Oldest, node.Draining, node.StartedAt,
            sessions);
    }

    private static async Task<byte[]?> ReadAsync(Stream body, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + n > MaxRequest) return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
}

/// <summary>Drain, resume and resync, on this node, written to the connection log with who asked.</summary>
public static class NodeActions
{
    public static async Task DrainAsync(IServiceProvider services, bool drain, string? by, int? viaNode)
    {
        var node = services.GetRequiredService<ClusterNode>();
        if (node.Draining == drain) return;
        node.SetDraining(drain);
        services.GetRequiredService<ILogger<ClusterNode>>().LogWarning("node {Node} {Action} by {By}", node.Id,
            drain ? "draining" : "resumed", by);
        await LogAsync(services, by, drain ? $"drained node {node.Id}" : $"resumed node {node.Id}", viaNode);
    }

    public static async Task<(bool Ok, string Message)> ResyncAsync(IServiceProvider services, string? by, int? viaNode,
        CancellationToken cancel)
    {
        var node = services.GetRequiredService<ClusterNode>();
        var result = await services.GetRequiredService<SyncService>().ResyncAsync(cancel);
        if (result.Ok) await LogAsync(services, by, $"resynced node {node.Id} from scratch", viaNode);
        return result;
    }

    private static async Task LogAsync(IServiceProvider services, string? by, string what, int? viaNode)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await services.GetRequiredService<ConnectionLogger>().AddAsync(new ConnectionLog
        {
            Kind = LogKind.Node,
            Upn = by,
            ConnectionName = "",
            StartedAt = now,
            EndedAt = now,
            EndReason = viaNode is { } via ? $"{what} (asked through node {via})" : what,
        });
    }
}
