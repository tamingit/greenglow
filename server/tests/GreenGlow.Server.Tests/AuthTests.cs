using System.Net;
using System.Text.Json;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

public sealed class AuthTests
{
    private static RelayFactory Factory() => RelayFactory.For("test=tcp://127.0.0.1:1");

    private static HttpClient Anonymous(RelayFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task TheApiAndWebSocketNeedASignIn()
    {
        using var factory = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/targets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).PostAsync("/api/sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync($"/ws/session/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Anonymous(factory).GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task SigningInRecordsTheUser()
    {
        using var factory = Factory();
        var client = await factory.SignedInClientAsync("Pat.Doe@example.com");
        var me = JsonDocument.Parse(await client.GetStringAsync("/api/me")).RootElement;
        Assert.Equal("Pat.Doe@example.com", me.GetProperty("upn").GetString());
        Assert.Equal(AuthSetup.DevObjectId("pat.doe@example.com"), me.GetProperty("id").GetString());

        await using var db = factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();
        var user = await db.Users.SingleAsync();
        Assert.Equal("Pat.Doe@example.com", user.Upn);
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public async Task ARemovedUserLosesAccessAtOnce()
    {
        using var factory = Factory();
        var client = await factory.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext())
        {
            db.Users.RemoveRange(db.Users);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task ChangesMustComeFromAnAllowedOrigin()
    {
        using var factory = Factory();
        var cookie = await factory.SignInAsync();
        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/auth/signout", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/auth/signout", null)).StatusCode);

        var signedIn = await factory.SignedInClientAsync();
        var response = await signedIn.PostAsync("/auth/signout", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("greenglow.auth=;"));
    }

    [Fact]
    public async Task SignInGoesToTheDevLoginWithALocalReturnPath()
    {
        using var factory = Factory();
        var response = await Anonymous(factory).GetAsync("/auth/signin?returnUrl=/admin.html");
        Assert.Equal("/auth/dev?returnUrl=%2Fadmin.html", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/admin.html?x=1", "/admin.html?x=1")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    public void ReturnPathsStayOnThisSite(string? asked, string expected) =>
        Assert.Equal(expected, AuthSetup.SafeReturn(asked));

    [Fact]
    public void TheDevLoginIsRefusedInProduction()
    {
        using var factory = new ProductionFactory(new Dictionary<string, string?> { ["Auth:DevLogin"] = "true" });
        var error = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Entra ID is not configured", (error as ConfigException ?? error.InnerException)?.Message ?? error.Message);
    }

    [Fact]
    public async Task TheDevLoginIsOffWhenEntraIsConfigured()
    {
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Entra:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["Entra:ClientId"] = "00000000-0000-0000-0000-000000000002",
            ["Entra:ClientSecret"] = "not-a-secret",
        });
        var client = Anonymous(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/dev")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    private sealed class ProductionFactory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("greenglow-test-").FullName;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Data:DbPath", Path.Combine(_dir, "greenglow.db"));
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }
    }
}

public sealed class SignInKeyTests
{
    [Fact]
    public async Task KeysAnEarlierVersionKeptInFilesAreImported()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-keys-").FullName;
        var dbPath = Path.Combine(dir, "greenglow.db");
        var settings = new Dictionary<string, string?> { ["Server:PublicUrl"] = T.Origin, ["Data:DbPath"] = dbPath };
        try
        {
            // A sign-in made with a key ring that lives only in files beside the database, as
            // before keys moved into the database.
            string cookie;
            using (var first = new RelayFactory(settings))
            {
                cookie = await first.SignInAsync();
                await using var db = first.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();
                Directory.CreateDirectory(Path.Combine(dir, "keys"));
                foreach (var key in await db.SigningKeys.ToListAsync())
                    await File.WriteAllTextAsync(Path.Combine(dir, "keys", key.Name + ".xml"), key.Xml);
                await db.SigningKeys.ExecuteDeleteAsync();
            }

            using var second = new RelayFactory(settings);
            var client = second.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        }
        finally
        {
            T.ClearPool(dbPath);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ASignInSurvivesARestartOnTheSameData()
    {
        var dir = Directory.CreateTempSubdirectory("greenglow-keys-").FullName;
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Data:DbPath"] = Path.Combine(dir, "greenglow.db"),
        };
        try
        {
            string cookie;
            using (var first = new RelayFactory(settings))
            {
                cookie = await first.SignInAsync();
                // The key ring is in the database (it syncs between nodes), not in files.
                await using var db = first.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();
                Assert.NotEmpty(await db.SigningKeys.ToListAsync());
            }
            using var second = new RelayFactory(settings);
            var client = second.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        }
        finally
        {
            T.ClearPool(Path.Combine(dir, "greenglow.db"));
            Directory.Delete(dir, recursive: true);
        }
    }
}
