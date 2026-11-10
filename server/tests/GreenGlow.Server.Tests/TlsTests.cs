using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Tests;

public sealed class TlsTests : IDisposable
{
    private readonly TestPki _pki = new();
    private readonly TestPki _otherPki = new("Some Other CA");
    private readonly TempCaFile _caFile;
    private readonly TempCaFile _otherCaFile;

    public TlsTests()
    {
        _caFile = new TempCaFile(_pki.Ca);
        _otherCaFile = new TempCaFile(_otherPki.Ca);
    }

    public void Dispose()
    {
        _caFile.Dispose();
        _otherCaFile.Dispose();
        _pki.Dispose();
        _otherPki.Dispose();
    }

    /// <summary>A fake IBM i behind a TLS-terminating proxy, and a server whose "secure" target is that proxy.</summary>
    private static async Task<(GreenGlow.FakeHost.FakeIbmI Host, Proxy Proxy, RelayFactory Factory, Rig Rig)> StartAsync(
        X509Certificate2 cert, string? caFile, Task? gate = null)
    {
        var host = await GreenGlow.FakeHost.FakeIbmI.StartAsync();
        var proxy = new Proxy(host.Port, cert, gate);
        var factory = RelayFactory.For($"secure=tls://localhost:{proxy.Port}", caFile: caFile);
        return (host, proxy, factory, Rig.Wrap(host, factory));
    }

    [Fact]
    public async Task TlsTargetWithRightCaReachesTheSignOnScreen()
    {
        using var cert = _pki.IssueServer("localhost");
        var (host, proxy, factory, rig) = await StartAsync(cert, _caFile.Path);
        await using var _host = host;
        await using var _proxy = proxy;
        using var _factory = factory;
        var (opened, browser) = await rig.OpenAndAttachAsync("secure");
        await using var browserScope = browser;
        Assert.True(opened.Body.GetProperty("secure").GetBoolean());
        var screen = await browser.WaitForTextAsync("Sign On");
        Assert.Equal(2, screen.InputFields);
        Assert.Equal(1, host.Open);

        var mark = browser.Mark;
        await browser.SignOnAsync("pat", "pw");
        await browser.WaitForTextAsync("IBM i Main Menu", mark);
        // The server does not reveal the host or port in the session list.
        var (raw, _) = await rig.ListAsync();
        Assert.DoesNotContain(proxy.Port.ToString(), raw);
    }

    [Fact]
    public async Task WrongCaIsRejected()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cert = _pki.IssueServer("localhost");
        var (host, proxy, factory, rig) = await StartAsync(cert, _otherCaFile.Path, gate.Task);
        await using var _host = host;
        await using var _proxy = proxy;
        using var _factory = factory;
        await ExpectRejectedAsync(rig, gate);
    }

    [Fact]
    public async Task CertificateForAnotherNameIsRejected()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cert = _pki.IssueServer("ibmi.example.com");
        var (host, proxy, factory, rig) = await StartAsync(cert, _caFile.Path, gate.Task);
        await using var _host = host;
        await using var _proxy = proxy;
        using var _factory = factory;
        await ExpectRejectedAsync(rig, gate);
    }

    private static async Task ExpectRejectedAsync(Rig rig, TaskCompletionSource gate)
    {
        var (_, browser) = await rig.OpenAndAttachAsync("secure");
        await using var browserScope = browser;
        gate.SetResult();
        var closed = await browser.WaitForTypeAsync("closed");
        Assert.Equal("host error certificate rejected", closed.GetProperty("reason").GetString());
        var (status, _) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        await rig.WaitNoSessionsAsync();
        // The log row closes just after the session leaves the list.
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync(Data.LogKind.Session)).Single().EndedAt is not null, "the log row to close");
        Assert.Equal("host error certificate rejected", (await rig.LogRowsAsync(Data.LogKind.Session)).Single().EndReason);
    }

    [Fact]
    public async Task WithoutACaAnyCertificateIsAcceptedAndReportedUnverified()
    {
        // From a CA no one configured, and for another name: only accepted because there is no CA.
        using var stranger = new TestPki("Unknown CA");
        using var cert = stranger.IssueServer("not-this-host.example");
        var (host, proxy, factory, rig) = await StartAsync(cert, caFile: null);
        await using var _host = host;
        await using var _proxy = proxy;
        using var _factory = factory;
        var (_, browser) = await rig.OpenAndAttachAsync("secure");
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        Assert.Equal(1, host.Open);

        var targets = await (await factory.SignedInClientAsync()).GetStringAsync("/api/targets");
        Assert.Contains("\"secure\":true,\"verified\":false", targets);
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("its certificate is not verified"));
    }

    [Fact]
    public async Task WithACaTheTargetIsReportedVerified()
    {
        using var cert = _pki.IssueServer("localhost");
        var (host, proxy, factory, _) = await StartAsync(cert, _caFile.Path);
        await using var _host = host;
        await using var _proxy = proxy;
        using var _factory = factory;
        var targets = await (await factory.SignedInClientAsync()).GetStringAsync("/api/targets");
        Assert.Contains("\"secure\":true,\"verified\":true", targets);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains("not verified"));
    }

    [Fact]
    public void VerifyAcceptsCertificateFromConfiguredCa()
    {
        using var cert = _pki.IssueServer("localhost");
        Assert.True(HostConnector.Verify(cert, SslPolicyErrors.None, [_pki.Ca]));
    }

    [Fact]
    public void VerifyIgnoresSystemChainErrorsButChecksAgainstConfiguredCa()
    {
        using var cert = _pki.IssueServer("localhost");
        // The system store does not know this CA, so the platform reports a chain error; that is expected.
        Assert.True(HostConnector.Verify(cert, SslPolicyErrors.RemoteCertificateChainErrors, [_pki.Ca]));
    }

    [Fact]
    public void VerifyRejectsOtherCa()
    {
        using var cert = _pki.IssueServer("localhost");
        Assert.False(HostConnector.Verify(cert, SslPolicyErrors.None, [_otherPki.Ca]));
    }

    [Fact]
    public void VerifyRejectsNameMismatchAndMissingCertificate()
    {
        using var cert = _pki.IssueServer("localhost");
        Assert.False(HostConnector.Verify(cert, SslPolicyErrors.RemoteCertificateNameMismatch, [_pki.Ca]));
        Assert.False(HostConnector.Verify(cert, SslPolicyErrors.RemoteCertificateNotAvailable, [_pki.Ca]));
        Assert.False(HostConnector.Verify(null, SslPolicyErrors.None, [_pki.Ca]));
    }

    [Fact]
    public void VerifyRejectsSelfSignedCertificateNotInTrust()
    {
        using var copy = X509CertificateLoader.LoadCertificate(_otherPki.Ca.RawData);
        Assert.False(HostConnector.Verify(copy, SslPolicyErrors.None, [_pki.Ca]));
    }

    [Fact]
    public void CreateReadsTheCaWhenTheFileExists()
    {
        Assert.Null(HostConnector.Create("/nonexistent/ca.pem").Ca);
        Assert.Equal(_pki.Ca.Subject, HostConnector.Create(_caFile.Path).Ca?.Subject);
    }

    [Fact]
    public void CreateRejectsANonPemCaFile()
    {
        var junk = Path.Combine(Path.GetDirectoryName(_caFile.Path)!, "junk.pem");
        File.WriteAllText(junk, "not a certificate");
        Assert.Throws<ConfigException>(() => HostConnector.Create(junk));
    }

    [Fact]
    public void AnInlinePemCaWinsOverTheFile()
    {
        using var other = new TestPki("Other CA");
        var connector = HostConnector.Create(_caFile.Path, other.Ca.ExportCertificatePem());
        Assert.Equal(other.Ca.Subject, connector.Ca?.Subject);
        Assert.Throws<ConfigException>(() => HostConnector.Create("/nonexistent/ca.pem", "not a certificate"));
        Assert.Null(HostConnector.Create("/nonexistent/ca.pem", "  ").Ca);
        // A deployment variable may hold the PEM on one line, spaces for newlines.
        var oneLine = other.Ca.ExportCertificatePem().Replace("\r", "").Replace("\n", " ");
        Assert.Equal(other.Ca.Subject, HostConnector.Create("/nonexistent/ca.pem", oneLine).Ca?.Subject);
    }
}
