using System.Collections.Concurrent;
using System.Text.Json;

namespace GreenGlow.Server.Cluster;

/// <summary>A peer couldn't be asked, or refused: the reason, for the admin page.</summary>
public sealed class PeerException(string message) : Exception(message);

/// <summary>The peer protocol: a node speaks Version and the one before it.</summary>
public static class PeerProtocol
{
    public const int Version = 1;
    public const int Oldest = 1;

    public const string FromHeader = "X-GreenGlow-From";
    public const string TimeHeader = "X-GreenGlow-Time";

    /// <summary>How far apart two nodes' clocks may be, and how long a request's nonce is remembered.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>A request: the protocol versions the sender speaks, and what it asks.</summary>
public sealed record PeerRequest(int Protocol, int Oldest, JsonElement Body);

/// <summary>An answer: the protocol version used, and the result or why there is none.</summary>
public sealed record PeerAnswer(int Protocol, string? Error, JsonElement? Body);

/// <summary>How this node reaches its peers; tests put in-process servers here.</summary>
public class PeerTransport
{
    public virtual HttpMessageHandler HandlerFor(int node) =>
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(5) };
}

/// <summary>Calls another node's peer API, sealed both ways.</summary>
public sealed class PeerClient(ClusterOptions options, PeerTransport transport, TimeProvider clock)
{
    private readonly Seal? _seal = options.Secret is { Length: > 0 } secret ? new Seal(secret) : null;
    private readonly ConcurrentDictionary<int, HttpClient> _clients = new();
    private readonly ConcurrentDictionary<int, int> _protocols = new();

    /// <summary>The protocol version each peer last answered with.</summary>
    public IReadOnlyDictionary<int, int> Protocols => _protocols;

    public async Task<T> CallAsync<T>(int peer, string op, object body, TimeSpan timeout, CancellationToken cancel = default)
    {
        if (_seal is null || !options.PeerUrls.TryGetValue(peer, out var url)) throw new PeerException($"Node {peer} isn't a peer of this node.");
        var time = clock.GetUtcNow().ToUnixTimeSeconds();
        var plain = JsonSerializer.SerializeToUtf8Bytes(
            new PeerRequest(PeerProtocol.Version, PeerProtocol.Oldest, JsonSerializer.SerializeToElement(body, PeerProtocol.Json)),
            PeerProtocol.Json);
        var sealedBody = _seal.Close(plain, Seal.RequestContext(op, options.NodeId, peer, time));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.ToString().TrimEnd('/')}/cluster/{op}")
        {
            Content = new ByteArrayContent(sealedBody),
        };
        request.Headers.Add(PeerProtocol.FromHeader, options.NodeId.ToString());
        request.Headers.Add(PeerProtocol.TimeHeader, time.ToString());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await Client(peer).SendAsync(request, limit.Token);
        }
        catch (HttpRequestException e)
        {
            throw new PeerException($"Unreachable: {e.Message}");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PeerException($"No answer within {timeout.TotalSeconds:0} seconds.");
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new PeerException("Refused this node: check that both have the same Cluster__Secret, list each other as peers, and have clocks within two minutes.");
            if (!response.IsSuccessStatusCode) throw new PeerException($"Answered {(int)response.StatusCode} (is the URL the peer port?).");
            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(limit.Token);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                throw new PeerException($"The answer broke off: {e.Message}");
            }
            var opened = _seal.Open(bytes, Seal.ResponseContext(Seal.NonceOf(sealedBody)))
                         ?? throw new PeerException("Its answer wasn't sealed for this request.");
            var answer = JsonSerializer.Deserialize<PeerAnswer>(opened, PeerProtocol.Json) ?? throw new PeerException("An empty answer.");
            _protocols[peer] = answer.Protocol;
            if (answer.Error is { } error) throw new PeerException(error);
            return answer.Body is { } result
                ? result.Deserialize<T>(PeerProtocol.Json) ?? throw new PeerException("An empty answer.")
                : throw new PeerException("An empty answer.");
        }
    }

    private HttpClient Client(int peer) =>
        _clients.GetOrAdd(peer, id => new HttpClient(transport.HandlerFor(id)) { Timeout = Timeout.InfiniteTimeSpan });
}
