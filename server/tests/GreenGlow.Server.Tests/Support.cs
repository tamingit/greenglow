using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GreenGlow.Server.Tests;

public static class T
{
    // Generous for a cold first run on a busy CI machine; a hang still fails.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    public const string Origin = "http://localhost:5080";

    public static CancellationToken Token() => new CancellationTokenSource(Timeout).Token;

    /// <summary>Closes the pooled connections to one test database, so its file can be deleted.</summary>
    public static void ClearPool(string dbPath) =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.GetFullPath(dbPath)}"));

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? Status, string? Reason)>
        ReceiveAsync(WebSocket ws, int? expectBytes = null)
    {
        var cancel = Token();
        var buffer = new byte[4096];
        var data = new List<byte>();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cancel);
            if (r.MessageType == WebSocketMessageType.Close)
                return (r.MessageType, data.ToArray(), r.CloseStatus, r.CloseStatusDescription);
            data.AddRange(buffer.AsSpan(0, r.Count).ToArray());
            if (r.EndOfMessage && (expectBytes is null || data.Count >= expectBytes))
                return (r.MessageType, data.ToArray(), null, null);
        }
    }

    public static async Task<(WebSocketCloseStatus? Status, string? Reason)> ExpectCloseAsync(WebSocket ws)
    {
        while (true)
        {
            var m = await ReceiveAsync(ws);
            if (m.Type == WebSocketMessageType.Close) return (m.Status, m.Reason);
        }
    }
}

public sealed record LogEntry(string Category, LogLevel Level, string Message, string State);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);

    public void Dispose() { }

    private sealed class Capture(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state is IEnumerable<KeyValuePair<string, object?>> kv
                ? string.Join(";", kv.Select(p => $"{p.Key}={p.Value}"))
                : state?.ToString() ?? "";
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception) + (exception?.ToString() ?? ""), pairs));
        }
    }
}

public sealed class RelayFactory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
{
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Each factory has its own database, deleted with it.</summary>
    public string DbDirectory { get; } = Directory.CreateTempSubdirectory("greenglow-test-").FullName;

    /// <summary>Extra service registrations, such as a fake clock; set before first use.</summary>
    public Action<IServiceCollection>? ExtraServices { get; set; }

    /// <summary>The host environment; Testing unless a test needs Production.</summary>
    public string Environment { get; set; } = "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("Data:DbPath", Path.Combine(DbDirectory, "greenglow.db"));
        builder.UseSetting("Auth:DevLogin", "true");
        // Sessions end as soon as their last browser leaves unless a test asks otherwise.
        builder.UseSetting("Sessions:IdleMinutes", "0");
        // Everyone is in the stand-in directory's "Everyone" group, which SeedJson grants.
        builder.UseSetting("Dev:Groups", "Everyone=*");
        // Off by default so unrelated tests aren't throttled; RateLimitTests turns it on.
        builder.UseSetting("RateLimit:Enabled", "false");
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<ILoggerProvider>(Logs);
            ExtraServices?.Invoke(s);
        });
    }

    public static RelayFactory For(string targets, string labels = "", int? max = null, string? caFile = null)
    {
        var s = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Server:AllowedOrigins:0"] = "http://localhost:5173/",
            ["Seed:ConnectionsJson"] = SeedJson(targets, labels),
        };
        if (max is not null) s["Sessions:MaxConnections"] = max.ToString();
        if (caFile is not null) s["Relay:CaFile"] = caFile;
        return new RelayFactory(s);
    }

    /// <summary>
    /// Seed JSON for test connections given as "name=url,..." with optional "name=label,..."
    /// labels, and an optional per-user session limit for all of them.
    /// </summary>
    public static readonly string EveryoneGroup = GreenGlow.Server.People.LocalDirectory.GroupId("Everyone");

    public static string SeedJson(string targets, string labels = "", int maxSessionsPerUser = 3)
    {
        static Dictionary<string, string> Pairs(string text) => text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('=', 2))
            .ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim() : "");
        var named = Pairs(labels);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            connections = Pairs(targets).Select(p => new
            {
                name = p.Key,
                label = named.GetValueOrDefault(p.Key, p.Key),
                url = p.Value,
                maxSessionsPerUser,
                isTest = true,
                access = new[] { new { type = "group", id = EveryoneGroup, name = "Everyone" } },
            }),
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // This server's pool only: clearing every pool would close other tests' connections mid-request.
        T.ClearPool(settings.GetValueOrDefault("Data:DbPath") ?? Path.Combine(DbDirectory, "greenglow.db"));
        try
        {
            Directory.Delete(DbDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Still open on some platforms; the temp directory is cleaned up eventually.
        }
    }

    public const string DefaultUpn = "tester@example.com";
    private readonly Dictionary<string, string> _cookies = [];

    /// <summary>Signs in through the dev login and returns the auth cookie ("name=value").</summary>
    public async Task<string> SignInAsync(string upn = DefaultUpn, string name = "Test User")
    {
        if (_cookies.TryGetValue(upn, out var cached)) return cached;
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/dev")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["upn"] = upn, ["name"] = name, ["returnUrl"] = "/" }),
        };
        request.Headers.Add("Origin", T.Origin);
        var response = await client.SendAsync(request, T.Token());
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("greenglow.auth="));
        return _cookies[upn] = cookie[..cookie.IndexOf(';')];
    }

    /// <summary>An HTTP client signed in as `upn`, sending this site's Origin.</summary>
    public async Task<HttpClient> SignedInClientAsync(string upn = DefaultUpn)
    {
        var cookie = await SignInAsync(upn);
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("Origin", T.Origin);
        return client;
    }

    /// <summary>Opens the WebSocket of a host session; throws (with the status in the message) if refused.</summary>
    /// <param name="share">A share link's token, for a recipient.</param>
    public async Task<WebSocket> ConnectAsync(Guid sessionId, string? origin = T.Origin, string? upn = DefaultUpn, string? share = null,
        bool takeOver = false)
    {
        var cookie = upn is null ? null : await SignInAsync(upn);
        var client = Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            if (origin is not null) r.Headers["Origin"] = origin;
            if (cookie is not null) r.Headers["Cookie"] = cookie;
        };
        var query = share is not null ? $"?share={Uri.EscapeDataString(share)}" : takeOver ? "?takeover=1" : "";
        return await client.ConnectAsync(new Uri($"ws://localhost/ws/session/{sessionId}{query}"), T.Token());
    }

    /// <summary>A page's /ws/user socket, which the server pushes the user's session list to.</summary>
    public async Task<WebSocket> ConnectUserAsync(string? upn = DefaultUpn, string? origin = T.Origin)
    {
        var cookie = upn is null ? null : await SignInAsync(upn);
        var client = Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            if (origin is not null) r.Headers["Origin"] = origin;
            if (cookie is not null) r.Headers["Cookie"] = cookie;
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws/user"), T.Token());
    }
}

/// <summary>Throwaway certificates, made in memory. Nothing is written to the repository.</summary>
public sealed class TestPki : IDisposable
{
    public X509Certificate2 Ca { get; }
    private readonly RSA _caKey;

    private TestPki(X509Certificate2 ca, RSA key)
    {
        Ca = ca;
        _caKey = key;
    }

    public TestPki(string name = "GreenGlow Test CA")
    {
        _caKey = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", _caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>An intermediate CA issued by this one, which can issue certificates of its own.</summary>
    public TestPki IssueIntermediate(string name)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        using var signed = req.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20), serial);
        return new TestPki(signed.CopyWithPrivateKey(key), key);
    }

    public X509Certificate2 IssueServer(string dnsName)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        using var signed = req.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Linux SslStream needs a key it can use after the temporary key is disposed.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        Ca.Dispose();
        _caKey.Dispose();
    }
}

/// <summary>A temp directory holding a CA PEM file, deleted on dispose.</summary>
public sealed class TempCaFile : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("greenglow-ca-").FullName;
    public string Path { get; }

    public TempCaFile(X509Certificate2 ca)
    {
        Path = System.IO.Path.Combine(_dir, "ca.pem");
        File.WriteAllText(Path, ca.ExportCertificatePem());
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
