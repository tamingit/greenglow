using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.HostServers;

/// <summary>
/// The IBM i's port mapper (as-svrmap, port 449): asked for a host server by name, it answers
/// with the port that server listens on, for systems where the ports were moved from IBM's
/// defaults. The request is the service name in ASCII and nothing else; the reply is five
/// bytes, '+' and the port (big-endian), or anything else for a name it doesn't know. The port
/// mapper itself is always plain TCP, as it is for ACS: what it answers is only a port number,
/// and a tls:// connection still verifies the certificate of whatever listens there.
/// </summary>
public static class PortMapper
{
    public const int Port = 449;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> LookupAsync(string host, int port, string serviceName, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(serviceName), timeout.Token);
            var reply = new byte[5];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            var found = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(1));
            if (reply[0] != (byte)'+' || found is < 1 or > 65535)
                throw new SignOnException(SignOnFailure.Unreachable, $"The IBM i's port mapper has no port for {serviceName}.");
            return found;
        }
        catch (Exception e) when (e is SocketException or IOException or EndOfStreamException
                                      || e is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new SignOnException(SignOnFailure.Unreachable,
                $"Couldn't reach the IBM i's port mapper ({HostServerClient.Describe(e)}).");
        }
    }
}

/// <summary>Where the IBM i's host servers are (HostServers__*). The ports here are for the fake IBM i in tests.</summary>
public sealed class HostServerOptions
{
    /// <summary>Another port for the port mapper. Production always uses 449.</summary>
    public int? MapperPort { get; set; }

    /// <summary>Another port for the sign-on server. Production always uses IBM's: 8476, and 9476 over TLS.</summary>
    public int? SignOnPort { get; set; }

    public int? SignOnSecurePort { get; set; }
}

/// <summary>
/// Finds a connection's host servers: on the Telnet connection's host, over TLS when it is,
/// with its TLS trust. The port comes from the IBM i's port mapper when the connection says to
/// use it (remembered for a few minutes), and otherwise is IBM's default.
/// </summary>
public sealed class HostEndpoints(HostServerOptions options, PrinterOptions printer, TimeProvider clock)
{
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Host, string Service), (int Port, DateTimeOffset At)> _ports = new();

    /// <summary>The service's default port for a connection, without asking the port mapper.</summary>
    public Target Default(Target telnet, HostService service)
    {
        var (plain, secure) = service == HostService.NetworkPrint
            ? (printer.Port, printer.SecurePort)
            : service == HostService.SignOn ? (options.SignOnPort, options.SignOnSecurePort) : (null, null);
        return telnet with { Port = telnet.Secure ? secure ?? service.SecurePort : plain ?? service.Port };
    }

    /// <summary>The service's endpoint: from the port mapper when the connection uses it. Throws SignOnException (Unreachable).</summary>
    public async Task<Target> ForAsync(Target telnet, HostService service, CancellationToken cancel)
    {
        if (!telnet.UsePortMapper) return Default(telnet, service);
        var key = (telnet.Host.ToLowerInvariant(), service.MapName(telnet.Secure));
        var now = clock.GetUtcNow();
        if (_ports.TryGetValue(key, out var known) && now - known.At < Remembered) return telnet with { Port = known.Port };
        var port = await PortMapper.LookupAsync(telnet.Host, options.MapperPort ?? PortMapper.Port, key.Item2, cancel);
        _ports[key] = (port, now);
        return telnet with { Port = port };
    }

    /// <summary>
    /// The file server's endpoint. The port mapper's port comes first, asked every time the
    /// mapper is reachable; IBM's default (8473, or 9473 over TLS) is the fallback when the mapper
    /// can't say (it is down, or doesn't know the service). A refused sign-on is never retried here.
    /// </summary>
    public async Task<Target> ForFileServerAsync(Target telnet, CancellationToken cancel)
    {
        try
        {
            return await ForAsync(telnet with { UsePortMapper = true }, HostService.FileServer, cancel);
        }
        catch (SignOnException e) when (e.Failure == SignOnFailure.Unreachable && !cancel.IsCancellationRequested)
        {
            Forget(telnet, HostService.FileServer);
            return Default(telnet, HostService.FileServer);
        }
    }

    /// <summary>Forgets a remembered port, after a connection to it failed: the next lookup asks again.</summary>
    public void Forget(Target telnet, HostService service) =>
        _ports.TryRemove((telnet.Host.ToLowerInvariant(), service.MapName(telnet.Secure)), out _);
}
