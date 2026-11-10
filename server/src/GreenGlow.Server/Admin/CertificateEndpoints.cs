using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;
using GreenGlow.Server.Web;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Admin;

/// <summary>
/// /api/admin/connections/{id}/certificate: an administrator looks at the certificate a tls://
/// connection's IBM i presents and trusts its CA for that connection only, alongside the server's
/// own CA (decided 7 October 2026). Trusting a CA whose certificate doesn't name the host asks
/// first, and then remembers the name accepted. Each change is written to the connection log.
/// </summary>
public static class CertificateEndpoints
{
    /// <summary>
    /// Trust: `sha256` is the CA as the check showed it, so a certificate that changed since is
    /// refused; `pem` is a CA pasted in when the IBM i doesn't send its root; `acceptName` is a
    /// name the certificate has, accepted in place of the host.
    /// </summary>
    public sealed record TrustRequest(string? Sha256, string? Pem, string? AcceptName);

    public static void MapCertificateEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapPost("/connections/{id:guid}/certificate/check", async (Guid id, IDbContextFactory<GreenGlowDb> dbs,
            HostConnector connector, CancellationToken cancel) =>
        {
            var connection = await SecureConnectionAsync(dbs, id);
            if (connection is null) return Results.NotFound();
            var target = TargetCatalog.ToTarget(connection);
            var (chain, problem) = await InspectAsync(target, cancel);
            if (problem is not null) return problem;
            var leaf = chain[0];
            var top = chain[^1];
            return Results.Json(new
            {
                certificates = chain.Select(Describe),
                names = Names(leaf),
                host = connection.Host,
                hostMatches = leaf.MatchesHostname(connection.Host),
                // The CA "Trust" would store: the top of the chain if it is self-signed; else a pasted one.
                trustable = HostConnector.IsSelfIssued(top) ? Fingerprint(top) : null,
                verified = connector.TrustFor(target) is { Count: > 0 } trust
                           && (connection.TrustedName is { } name ? leaf.MatchesHostname(name) : leaf.MatchesHostname(connection.Host))
                           && HostConnector.ChainsTo(leaf, trust, [.. chain.Skip(1)]),
            });
        });

        admin.MapPost("/connections/{id:guid}/certificate/trust", async (Guid id, TrustRequest request, HttpContext context,
            IDbContextFactory<GreenGlowDb> dbs, HostConnector connector, TimeProvider clock, CancellationToken cancel) =>
        {
            await using var db = await dbs.CreateDbContextAsync(cancel);
            var connection = await db.Connections.Include(c => c.Grants).FirstOrDefaultAsync(c => c.Id == id, cancel);
            if (connection is null || !connection.Secure) return Results.NotFound();
            var (chain, problem) = await InspectAsync(TargetCatalog.ToTarget(connection), cancel);
            if (problem is not null) return problem;
            var leaf = chain[0];

            X509Certificate2 ca;
            if (!string.IsNullOrWhiteSpace(request.Pem))
            {
                X509Certificate2Collection pasted = [];
                try
                {
                    pasted.ImportFromPem(request.Pem);
                }
                catch (CryptographicException)
                {
                    return Problem("pem", "That isn't a PEM certificate.");
                }
                if (pasted.Count != 1) return Problem("pem", "Paste one certificate: the CA to trust.");
                ca = pasted[0];
                if (!IsCa(ca) && !HostConnector.IsSelfIssued(ca)) return Problem("pem", "That certificate isn't a CA.");
            }
            else
            {
                ca = chain[^1];
                if (!HostConnector.IsSelfIssued(ca))
                    return Problem("pem", "The IBM i doesn't send its root CA. Paste the root CA's PEM to trust it.");
                if (!string.Equals(request.Sha256, Fingerprint(ca), StringComparison.OrdinalIgnoreCase))
                    return Results.Conflict(new
                    {
                        error = "changed",
                        message = "The IBM i's certificate changed since it was checked. Check it again.",
                    });
            }
            if (!HostConnector.ChainsTo(leaf, [ca], [.. chain.Skip(1)]))
                return Problem("pem", "The IBM i's certificate wasn't issued by that CA.");

            string? acceptedName = null;
            if (!leaf.MatchesHostname(connection.Host))
            {
                var names = Names(leaf);
                if (string.IsNullOrWhiteSpace(request.AcceptName))
                    return Results.Conflict(new
                    {
                        error = "name-mismatch",
                        names,
                        message = $"The certificate is for {string.Join(", ", names)}, not {connection.Host}.",
                    });
                acceptedName = request.AcceptName.Trim();
                if (!leaf.MatchesHostname(acceptedName)) return Problem("acceptName", $"The certificate isn't for {acceptedName}.");
            }

            var admin = CurrentUser.From(context.User)!;
            var now = clock.GetUtcNow();
            connection.TrustedCaPem = ca.ExportCertificatePem();
            connection.TrustedName = acceptedName;
            connection.TrustedBy = admin.Upn;
            connection.TrustedAt = now;
            connection.UpdatedAt = now;
            db.Logs.Add(Entry(connection, admin, context, now, "trusted",
                $"Trusted {ca.Subject} (SHA-256 {Fingerprint(ca)})" + (acceptedName is null ? "" : $" for the name {acceptedName}")));
            await db.SaveChangesAsync(cancel);
            return Results.Json(ConnectionEndpoints.Describe(connection, 0, connector.Verifies));
        });

        admin.MapDelete("/connections/{id:guid}/certificate", async (Guid id, HttpContext context,
            IDbContextFactory<GreenGlowDb> dbs, HostConnector connector, TimeProvider clock) =>
        {
            await using var db = await dbs.CreateDbContextAsync();
            var connection = await db.Connections.Include(c => c.Grants).FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();
            if (connection.TrustedCaPem is null) return Results.Json(ConnectionEndpoints.Describe(connection, 0, connector.Verifies));
            var subject = X509Certificate2.CreateFromPem(connection.TrustedCaPem).Subject;
            var now = clock.GetUtcNow();
            connection.TrustedCaPem = null;
            connection.TrustedName = null;
            connection.TrustedBy = null;
            connection.TrustedAt = null;
            connection.UpdatedAt = now;
            db.Logs.Add(Entry(connection, CurrentUser.From(context.User)!, context, now, "removed", $"Stopped trusting {subject}"));
            await db.SaveChangesAsync();
            return Results.Json(ConnectionEndpoints.Describe(connection, 0, connector.Verifies));
        });
    }

    private static async Task<HostConnection?> SecureConnectionAsync(IDbContextFactory<GreenGlowDb> dbs, Guid id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Secure);
    }

    /// <summary>The certificates, or a problem response saying why they couldn't be read.</summary>
    private static async Task<(IReadOnlyList<X509Certificate2> Chain, IResult? Problem)> InspectAsync(
        Configuration.Target target, CancellationToken cancel)
    {
        try
        {
            return (await HostConnector.InspectAsync(target, cancel), null);
        }
        catch (Exception e) when (e is SocketException or AuthenticationException or IOException or OperationCanceledException)
        {
            return ([], Results.Json(new
            {
                error = "unreachable",
                message = $"Couldn't read the IBM i's certificate ({e.GetType().Name}). Is the address right, and does the port offer TLS?",
            }, statusCode: StatusCodes.Status502BadGateway));
        }
    }

    private static bool IsCa(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority);

    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>The names a certificate is for: its DNS names, else its common name.</summary>
    private static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        var dns = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames()).ToList();
        if (dns.Count > 0) return dns;
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(cn) ? [] : [cn];
    }

    private static object Describe(X509Certificate2 c) => new
    {
        subject = c.Subject,
        issuer = c.Issuer,
        notBefore = new DateTimeOffset(c.NotBefore.ToUniversalTime(), TimeSpan.Zero),
        notAfter = new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero),
        sha256 = Fingerprint(c),
        selfSigned = HostConnector.IsSelfIssued(c),
        isCa = IsCa(c),
    };

    private static ConnectionLog Entry(HostConnection connection, CurrentUser admin, HttpContext context,
        DateTimeOffset now, string what, string detail) => new()
    {
        Kind = LogKind.Certificate,
        UserId = admin.Id,
        Upn = admin.Upn,
        ClientIp = ClientAddress.Of(context),
        ConnectionName = connection.Name,
        Role = what,
        StartedAt = now,
        EndedAt = now,
        EndReason = detail,
    };

    private static IResult Problem(string field, string message) => Results.BadRequest(new { error = field, message });
}
