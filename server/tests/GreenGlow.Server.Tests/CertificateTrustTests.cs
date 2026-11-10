using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GreenGlow.Server.Admin;
using GreenGlow.Server.Data;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Tests;

/// <summary>
/// An administrator trusts a tls:// connection's CA on the admin page, for that connection only
/// (decided 7 October 2026). The server has no CA of its own here, so before that any
/// certificate is accepted, unverified.
/// </summary>
public sealed class CertificateTrustTests : IDisposable
{
    private const string Admin = "boss@example.com";
    private readonly TestPki _root = new("GreenGlow Test Root");
    private readonly TestPki _other = new("Some Other CA");

    public void Dispose()
    {
        _root.Dispose();
        _other.Dispose();
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required GreenGlow.FakeHost.FakeIbmI Host { get; init; }
        public required Proxy Proxy { get; init; }
        public required RelayFactory Factory { get; init; }
        public required Rig Rig { get; init; }
        public required HttpClient Admin { get; init; }
        public required Guid Id { get; init; }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await Proxy.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    /// <summary>A fake IBM i behind TLS with `cert` (and `sent` issuers), as the connection "secure".</summary>
    private static async Task<Setup> StartAsync(X509Certificate2 cert, X509Certificate2Collection? sent = null)
    {
        var host = await GreenGlow.FakeHost.FakeIbmI.StartAsync();
        var proxy = new Proxy(host.Port, cert, sent: sent);
        var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Entra:InitialAdminUpn"] = Admin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson($"secure=tls://localhost:{proxy.Port}"),
        });
        var admin = await factory.SignedInClientAsync(Admin);
        var id = (await Json(await admin.GetAsync("/api/admin/connections", T.Token()))).GetProperty("connections")
            .EnumerateArray().Single().GetProperty("id").GetGuid();
        return new Setup { Host = host, Proxy = proxy, Factory = factory, Rig = Rig.Wrap(host, factory), Admin = admin, Id = id };
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static Task<HttpResponseMessage> TrustAsync(Setup s, object body) =>
        s.Admin.PostAsJsonAsync($"/api/admin/connections/{s.Id}/certificate/trust", body, T.Token());

    private static async Task<bool> VerifiedAsync(Setup s) =>
        (await Json(await (await s.Factory.SignedInClientAsync()).GetAsync("/api/targets", T.Token())))
        .GetProperty("targets").EnumerateArray().Single().GetProperty("verified").GetBoolean();

    private static async Task SignOnScreenAsync(Setup s)
    {
        var (_, browser) = await s.Rig.OpenAndAttachAsync("secure");
        await using var scope = browser;
        await browser.WaitForTextAsync("Sign On");
    }

    private static async Task RefusedAsync(Setup s)
    {
        var (_, browser) = await s.Rig.OpenAndAttachAsync("secure");
        await using var scope = browser;
        var closed = await browser.WaitForTypeAsync("closed");
        Assert.Equal("host error certificate rejected", closed.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TheCheckShowsTheCertificateAndWhetherTheRootWasSent()
    {
        using var cert = _root.IssueServer("localhost");
        await using var s = await StartAsync(cert);
        var check = await Json(await s.Admin.PostAsync($"/api/admin/connections/{s.Id}/certificate/check", null, T.Token()));
        var leaf = check.GetProperty("certificates").EnumerateArray().Single();
        Assert.Equal("CN=localhost", leaf.GetProperty("subject").GetString());
        Assert.Equal("CN=GreenGlow Test Root", leaf.GetProperty("issuer").GetString());
        Assert.Equal(CertificateEndpoints.Fingerprint(cert), leaf.GetProperty("sha256").GetString());
        Assert.True(check.GetProperty("hostMatches").GetBoolean());
        // Only the leaf was sent: there is no root to trust without pasting it.
        Assert.Equal(JsonValueKind.Null, check.GetProperty("trustable").ValueKind);
        Assert.False(check.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task APastedRootIsTrustedForThatConnectionAndLogged()
    {
        using var cert = _root.IssueServer("localhost");
        await using var s = await StartAsync(cert);
        Assert.False(await VerifiedAsync(s));

        // Without the root, or with another CA, nothing is trusted.
        Assert.Equal(HttpStatusCode.BadRequest, (await TrustAsync(s, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TrustAsync(s, new { pem = _other.Ca.ExportCertificatePem() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TrustAsync(s, new { pem = "not a certificate" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TrustAsync(s, new { pem = cert.ExportCertificatePem() })).StatusCode);

        var trusted = await TrustAsync(s, new { pem = _root.Ca.ExportCertificatePem() });
        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        var body = await Json(trusted);
        Assert.True(body.GetProperty("verified").GetBoolean());
        var trust = body.GetProperty("trust");
        Assert.Equal("CN=GreenGlow Test Root", trust.GetProperty("subject").GetString());
        Assert.Equal(Admin, trust.GetProperty("by").GetString());
        Assert.Equal(JsonValueKind.Null, trust.GetProperty("name").ValueKind);
        Assert.True(await VerifiedAsync(s));
        await SignOnScreenAsync(s);

        var row = Assert.Single(await s.Rig.LogRowsAsync(LogKind.Certificate));
        Assert.Equal(("secure", Admin, "trusted"), (row.ConnectionName, row.Upn, row.Role));
        Assert.Contains("CN=GreenGlow Test Root", row.EndReason);
        var log = await Json(await s.Admin.GetAsync("/api/admin/log?kind=certificate", T.Token()));
        Assert.Equal("certificate", log.GetProperty("rows").EnumerateArray().Single().GetProperty("kind").GetString());
    }

    [Fact]
    public async Task OnceTrustedACertificateFromAnotherCaIsRefused()
    {
        using var cert = _root.IssueServer("localhost");
        await using var s = await StartAsync(cert);
        Assert.Equal(HttpStatusCode.OK, (await TrustAsync(s, new { pem = _root.Ca.ExportCertificatePem() })).StatusCode);

        // The same address now answers with a certificate from a CA no one trusted.
        using var stranger = _other.IssueServer("localhost");
        await using var impostor = new Proxy(s.Host.Port, stranger);
        await s.Admin.PutAsJsonAsync($"/api/admin/connections/{s.Id}", new { url = $"tls://localhost:{impostor.Port}" }, T.Token());
        await RefusedAsync(s);
    }

    [Fact]
    public async Task ACertificateForAnotherNameIsTrustedOnlyOnceTheNameIsAccepted()
    {
        using var cert = _root.IssueServer("ibmi.example.com");
        await using var s = await StartAsync(cert);
        var pem = _root.Ca.ExportCertificatePem();

        var asked = await TrustAsync(s, new { pem });
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var question = await Json(asked);
        Assert.Equal("name-mismatch", question.GetProperty("error").GetString());
        Assert.Equal(["ibmi.example.com"], question.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.False(await VerifiedAsync(s));

        Assert.Equal(HttpStatusCode.BadRequest, (await TrustAsync(s, new { pem, acceptName = "other.example.com" })).StatusCode);
        var accepted = await TrustAsync(s, new { pem, acceptName = "ibmi.example.com" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("ibmi.example.com", (await Json(accepted)).GetProperty("trust").GetProperty("name").GetString());
        await SignOnScreenAsync(s);
    }

    [Fact]
    public async Task TheRootIsTrustedThroughTheIntermediatesTheIbmISends()
    {
        using var intermediate = _root.IssueIntermediate("GreenGlow Test Issuing CA");
        using var cert = intermediate.IssueServer("localhost");
        await using var s = await StartAsync(cert, [intermediate.Ca]);
        var check = await Json(await s.Admin.PostAsync($"/api/admin/connections/{s.Id}/certificate/check", null, T.Token()));
        Assert.Equal(["CN=localhost", "CN=GreenGlow Test Issuing CA"],
            check.GetProperty("certificates").EnumerateArray().Select(c => c.GetProperty("subject").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await TrustAsync(s, new { pem = _root.Ca.ExportCertificatePem() })).StatusCode);
        await SignOnScreenAsync(s);
    }

    [Fact]
    public async Task ACertificateAtTheTopOfItsChainIsTrustedAsTheCheckShowedIt()
    {
        // A self-signed IBM i certificate is its own root. (A root sent after intermediates is
        // found the same way; .NET's TLS server here won't send one, so Order is tested alone.)
        using var cert = SelfSigned("localhost");
        await using var s = await StartAsync(cert);
        var check = await Json(await s.Admin.PostAsync($"/api/admin/connections/{s.Id}/certificate/check", null, T.Token()));
        var trustable = check.GetProperty("trustable").GetString();
        Assert.Equal(CertificateEndpoints.Fingerprint(cert), trustable);

        // A fingerprint other than the one shown means the certificate changed: check again.
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(s, new { sha256 = new string('0', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TrustAsync(s, new { sha256 = trustable })).StatusCode);
        Assert.True(await VerifiedAsync(s));
        await SignOnScreenAsync(s);
    }

    private static X509Certificate2 SelfSigned(string dnsName)
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public async Task RemovingTheTrustGoesBackToUnverifiedAndIsLogged()
    {
        using var cert = _root.IssueServer("localhost");
        await using var s = await StartAsync(cert);
        await TrustAsync(s, new { pem = _root.Ca.ExportCertificatePem() });
        var removed = await s.Admin.DeleteAsync($"/api/admin/connections/{s.Id}/certificate", T.Token());
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(removed)).GetProperty("trust").ValueKind);
        Assert.False(await VerifiedAsync(s));
        Assert.Equal(["trusted", "removed"],
            (await s.Rig.LogRowsAsync(LogKind.Certificate)).OrderBy(r => r.Id).Select(r => r.Role));
    }

    [Fact]
    public async Task OnlyAdministratorsTrustAndOnlyTlsConnectionsHaveCertificates()
    {
        using var cert = _root.IssueServer("localhost");
        await using var s = await StartAsync(cert);
        var user = await s.Factory.SignedInClientAsync("pat@example.com");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsync($"/api/admin/connections/{s.Id}/certificate/check", null, T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync($"/api/admin/connections/{s.Id}/certificate/trust", new { }, T.Token())).StatusCode);
        await s.Admin.PutAsJsonAsync($"/api/admin/connections/{s.Id}", new { url = "tcp://localhost:23" }, T.Token());
        Assert.Equal(HttpStatusCode.NotFound,
            (await s.Admin.PostAsync($"/api/admin/connections/{s.Id}/certificate/check", null, T.Token())).StatusCode);
    }

    [Fact]
    public void TheChainIsOrderedFromTheLeafUp()
    {
        using var intermediate = _root.IssueIntermediate("GreenGlow Test Issuing CA");
        using var cert = intermediate.IssueServer("localhost");
        var chain = HostConnector.Order(cert, [_root.Ca, intermediate.Ca, cert]);
        Assert.Equal(["CN=localhost", "CN=GreenGlow Test Issuing CA", "CN=GreenGlow Test Root"], chain.Select(c => c.Subject));
        Assert.True(HostConnector.IsSelfIssued(chain[^1]));
    }

    [Fact]
    public void AnAcceptedNameReplacesTheHostInTheCheck()
    {
        using var cert = _root.IssueServer("ibmi.example.com");
        const System.Net.Security.SslPolicyErrors mismatch = System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
        Assert.False(HostConnector.Verify(cert, mismatch, [_root.Ca]));
        Assert.True(HostConnector.Verify(cert, mismatch, [_root.Ca], trustedName: "ibmi.example.com"));
        Assert.False(HostConnector.Verify(cert, mismatch, [_root.Ca], trustedName: "other.example.com"));
        Assert.False(HostConnector.Verify(cert, mismatch, [_other.Ca], trustedName: "ibmi.example.com"));
    }
}
