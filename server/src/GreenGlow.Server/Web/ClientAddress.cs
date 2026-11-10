using System.Net;

namespace GreenGlow.Server.Web;

public static class ClientAddress
{
    /// <summary>
    /// The browser's address as the connection log records it. Kestrel listens on IPv6 and IPv4
    /// together, so an IPv4 client arrives as an IPv4-mapped IPv6 address (::ffff:10.1.2.3); it is
    /// written as the IPv4 address it is. Behind the proxy, the forwarded address is used.
    /// </summary>
    public static string? Of(HttpContext context) => Format(context.Connection.RemoteIpAddress);

    public static string? Format(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();
}
