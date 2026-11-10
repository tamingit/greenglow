using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GreenGlow.Server.Configuration;

namespace GreenGlow.Server.Relay;

/// <summary>
/// Opens the Telnet connection to a target: plain TCP for tcp://, or TLS 1.2+ for tls://. The
/// trusted CAs are the server's (Relay:CaPem or Relay:CaFile), for every connection, and the
/// one an administrator trusted for this connection, if any (decided 7 October 2026). With
/// either, the certificate must chain to one of them (never the system store) and name the host,
/// or the name an administrator accepted for it. With neither, any certificate is accepted: the
/// connection is encrypted but the IBM i isn't verified, which the server warns about and the
/// page shows (decided 6 October 2026).
/// </summary>
public sealed class HostConnector(X509Certificate2Collection? trustedCa)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The CA from inline PEM text (Relay:CaPem, for deployments that pass it in a variable) or
    /// else the CA file if it exists. Without one, tls:// connections accept any certificate;
    /// they never fall back to the system store.
    /// </summary>
    public static HostConnector Create(string caFile, string? caPem = null)
    {
        X509Certificate2Collection ca = [];
        if (!string.IsNullOrWhiteSpace(caPem))
        {
            try
            {
                ca.ImportFromPem(caPem);
            }
            catch (CryptographicException)
            {
                throw new ConfigException("Relay:CaPem is not a PEM certificate");
            }
            if (ca.Count == 0) throw new ConfigException("Relay:CaPem is not a PEM certificate");
            return new HostConnector(ca);
        }
        if (!File.Exists(caFile)) return new HostConnector(null);
        try
        {
            ca.ImportFromPemFile(caFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ConfigException(
                $"Relay:CaFile {caFile} could not be read ({e.GetType().Name}). tls:// targets need the CA that issued the IBM i certificate.");
        }
        if (ca.Count == 0) throw new ConfigException($"Relay:CaFile {caFile} is not a PEM certificate");
        return new HostConnector(ca);
    }

    public X509Certificate2? Ca => trustedCa is { Count: > 0 } ? trustedCa[0] : null;

    /// <summary>Whether every tls:// certificate is verified (the server has a CA).</summary>
    public bool Verifies => trustedCa is { Count: > 0 };

    /// <summary>Whether this target's certificate is verified: by the server's CA, or its own.</summary>
    public bool VerifiesFor(Target target) => target.Secure && (Verifies || target.TrustedCaPem is not null);

    /// <summary>The CAs a target's certificate may chain to: the server's and its own. Empty: any.</summary>
    public X509Certificate2Collection TrustFor(Target target)
    {
        X509Certificate2Collection trust = [];
        if (trustedCa is not null) trust.AddRange(trustedCa);
        if (target.TrustedCaPem is { } pem) trust.Add(X509Certificate2.CreateFromPem(pem));
        return trust;
    }

    public async Task<(TcpClient Client, Stream Stream)> ConnectAsync(Target target, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(ConnectTimeout);
        var client = new TcpClient { NoDelay = true };
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            await client.ConnectAsync(target.Host, target.Port, timeout.Token);
            if (!target.Secure) return (client, client.GetStream());

            var trust = TrustFor(target);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    trust.Count == 0 || Verify(certificate, errors, trust, target.TrustedName, chain?.ChainPolicy.ExtraStore),
            }, timeout.Token);
            return (client, ssl);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Accepts a certificate only if it chains to a trusted CA, through the intermediates the
    /// IBM i sent (`sent`), and names the host, or `trustedName` when an administrator accepted
    /// that name. The system's own chain result is ignored: it uses the system store, which is
    /// not trusted here.
    /// </summary>
    internal static bool Verify(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2Collection ca,
        string? trustedName = null, X509Certificate2Collection? sent = null)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (trustedName is null ? (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 : !leaf.MatchesHostname(trustedName))
            return false;
        return ChainsTo(leaf, ca, sent);
    }

    /// <summary>Whether `leaf` chains to one of `ca`, through the intermediates in `sent`.</summary>
    internal static bool ChainsTo(X509Certificate2 leaf, X509Certificate2Collection ca, X509Certificate2Collection? sent = null)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(ca);
        if (sent is not null) chain.ChainPolicy.ExtraStore.AddRange(sent);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    /// <summary>
    /// Connects to a tls:// target only to see its certificate: the leaf first, then each issuer
    /// the IBM i sent, in order. Nothing is checked and no Telnet is spoken; the connection is
    /// closed at once. For an administrator deciding whether to trust it.
    /// </summary>
    public static async Task<IReadOnlyList<X509Certificate2>> InspectAsync(Target target, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(ConnectTimeout);
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(target.Host, target.Port, timeout.Token);
        X509Certificate2? leaf = null;
        List<X509Certificate2> sent = [];
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = target.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
            {
                if (certificate is not null) leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                if (chain is not null)
                    sent.AddRange(chain.ChainPolicy.ExtraStore.Select(c => X509CertificateLoader.LoadCertificate(c.RawData)));
                return true;
            },
        }, timeout.Token);
        if (leaf is null) throw new AuthenticationException("the IBM i sent no certificate");
        return Order(leaf, sent);
    }

    /// <summary>The leaf, then its issuer from `sent`, and so on until a self-signed one or a gap.</summary>
    internal static IReadOnlyList<X509Certificate2> Order(X509Certificate2 leaf, IEnumerable<X509Certificate2> sent)
    {
        var pool = sent.Where(c => c.Thumbprint != leaf.Thumbprint).ToList();
        List<X509Certificate2> chain = [leaf];
        var current = leaf;
        while (!IsSelfIssued(current) && chain.Count < 10)
        {
            var issuer = pool.FirstOrDefault(c => c.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData)
                                                  && chain.All(k => k.Thumbprint != c.Thumbprint));
            if (issuer is null) break;
            chain.Add(issuer);
            current = issuer;
        }
        return chain;
    }

    internal static bool IsSelfIssued(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
}
