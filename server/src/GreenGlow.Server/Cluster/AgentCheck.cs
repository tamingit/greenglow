using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// HAProxy's agent check (Cluster:AgentPort): each connection reads one line, "ready", or "drain"
/// while this node drains, so HAProxy stops sending it new users without its configuration
/// changing. Nothing is read from the connection.
/// </summary>
public sealed class AgentCheck(ClusterOptions options, ClusterNode node, ILogger<AgentCheck> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (options.AgentPort is not { } port) return;
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        log.LogInformation("agent check on port {Port}", port);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop);
                try
                {
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Answer(node.Draining)), stop);
                }
                catch (IOException)
                {
                    // HAProxy hung up first.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        finally
        {
            listener.Stop();
        }
    }

    public static string Answer(bool draining) => draining ? "drain\n" : "ready\n";
}
