using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.FakeHost;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>
/// First-run setup from Setup__*, and with IBM i sign-in, administrators the IBM i decides
/// (group profiles, special authorities) through the fake remote command server's QSYRUSRI.
/// Placeholder users and passwords only.
/// </summary>
public sealed class IbmAdminsTests
{
    private const string Breakglass = "breakglass placeholder 1";
    private const string Password = "Passw0rd!";

    private const string SeedJson = """
        {"connections":[{"name":"prod","label":"Production","url":"tcp://127.0.0.1:23","access":[{"type":"group","name":"Everyone"}]}]}
        """;

    private sealed class Ibm : IAsyncDisposable
    {
        public FakeAccounts Accounts { get; } = new();
        public FakeSignOn SignOn { get; }
        public FakeRemoteCommand RemoteCommand { get; }

        public Ibm()
        {
            SignOn = new FakeSignOn(Accounts);
            RemoteCommand = new FakeRemoteCommand(Accounts);
        }

        public async ValueTask DisposeAsync()
        {
            await SignOn.DisposeAsync();
            await RemoteCommand.DisposeAsync();
        }
    }

    private static RelayFactory Factory(Ibm? ibm, params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Auth:DevLogin"] = "false",
            ["Seed:ConnectionsJson"] = SeedJson,
        };
        if (ibm is not null)
        {
            settings["HostServers:SignOnPort"] = ibm.SignOn.Port.ToString();
            settings["HostServers:RemoteCommandPort"] = ibm.RemoteCommand.Port.ToString();
        }
        foreach (var (key, value) in extra) settings[key] = value;
        return new RelayFactory(settings);
    }

    /// <summary>Set up from the environment for IBM i sign-in on the seeded "prod".</summary>
    private static RelayFactory IbmFactory(Ibm ibm, string? groups = "GGADMIN", string? authorities = "*SECADM") => Factory(ibm,
        ("Setup:Method", "ibmi"), ("Setup:BreakglassPassword", Breakglass), ("Setup:IbmI:Connection", "prod"),
        ("Setup:IbmI:AdminGroups", groups), ("Setup:IbmI:AdminAuthorities", authorities));

    private static HttpClient Client(RelayFactory factory, string? cookie = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Origin", T.Origin);
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static string? Cookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
        && values.FirstOrDefault(c => c.StartsWith("greenglow.auth=") && !c.StartsWith("greenglow.auth=;")) is { } cookie
            ? cookie[..cookie.IndexOf(';')]
            : null;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(T.Token()) is { Length: > 0 } text ? JsonSerializer.Deserialize<JsonElement>(text) : default;

    /// <summary>Signs in; whether the account is an administrator afterwards.</summary>
    private static async Task<bool> SignInIsAdminAsync(RelayFactory factory, string user, string password = Password)
    {
        var response = await Client(factory).PostAsJsonAsync("/auth/password", new { user, password }, T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await Json(await Client(factory, Cookie(response)).GetAsync("/api/me", T.Token()));
        return me.GetProperty("isAdmin").GetBoolean();
    }

    private static async Task<string> BreakglassAsync(RelayFactory factory)
    {
        var response = await Client(factory).PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Cookie(response)!;
    }

    [Fact]
    public async Task TheEnvironmentSetsUpIbmISignInOnASeededConnection()
    {
        await using var ibm = new Ibm();
        using var factory = IbmFactory(ibm);
        var method = await Json(await Client(factory).GetAsync("/auth/method", T.Token()));
        Assert.Equal("ibmi", method.GetProperty("method").GetString());
        Assert.Equal("Production", method.GetProperty("system").GetString());
        Assert.False((await Json(await Client(factory).GetAsync("/auth/setup", T.Token()))).GetProperty("needed").GetBoolean());

        var admin = Client(factory, await BreakglassAsync(factory));
        var signin = await Json(await admin.GetAsync("/api/admin/signin", T.Token()));
        Assert.Equal(["GGADMIN"], signin.GetProperty("ibmAdmins").GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal(["*SECADM"], signin.GetProperty("ibmAdmins").GetProperty("authorities").EnumerateArray().Select(g => g.GetString()));
        // The seed came after setup, with its access list.
        var connection = (await Json(await admin.GetAsync("/api/admin/connections", T.Token()))).GetProperty("connections")[0];
        Assert.Equal("prod", connection.GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheIbmIDecidesWhoIsAnAdministratorAtEachSignIn()
    {
        await using var ibm = new Ibm();
        foreach (var user in new[] { "ALICE", "BOB", "CAROL", "DAVE" }) ibm.Accounts.Set(user, Password);
        ibm.Accounts.SetProfile("ALICE", group: "GGADMIN");
        ibm.Accounts.SetProfile("BOB", group: "STAFF", supplemental: ["OTHERS", "GGADMIN"]);
        ibm.Accounts.SetProfile("CAROL", authorities: ["*JOBCTL", "*SECADM"]);
        ibm.Accounts.SetProfile("DAVE", group: "STAFF", authorities: ["*JOBCTL"]);
        using var factory = IbmFactory(ibm);

        Assert.True(await SignInIsAdminAsync(factory, "alice"));
        Assert.True(await SignInIsAdminAsync(factory, "bob"));
        Assert.True(await SignInIsAdminAsync(factory, "carol"));
        Assert.False(await SignInIsAdminAsync(factory, "dave"));
        Assert.Equal(4, ibm.RemoteCommand.Calls);

        // Taken out of the group on the IBM i: no longer one at the next sign-in.
        ibm.Accounts.SetProfile("ALICE", group: "STAFF");
        Assert.False(await SignInIsAdminAsync(factory, "alice"));

        // Named on the admin page: the administrator's to take away, not the IBM i's.
        var admin = Client(factory, await BreakglassAsync(factory));
        var users = (await Json(await admin.GetAsync("/api/admin/users", T.Token()))).GetProperty("users").EnumerateArray().ToList();
        Assert.True(users.Single(u => u.GetProperty("user").GetString() == "CAROL").GetProperty("adminFromIbmI").GetBoolean());
        var dave = users.Single(u => u.GetProperty("user").GetString() == "DAVE").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/users/{dave}", new { isAdmin = true }, T.Token())).StatusCode);
        Assert.True(await SignInIsAdminAsync(factory, "dave"));
    }

    [Fact]
    public async Task WhenTheIbmICantSayTheGrantIsTakenAwayButTheSignInWorks()
    {
        await using var ibm = new Ibm();
        ibm.Accounts.Set("ALICE", Password);
        ibm.Accounts.SetProfile("ALICE", group: "GGADMIN");
        using var factory = IbmFactory(ibm);
        Assert.True(await SignInIsAdminAsync(factory, "alice"));

        ibm.RemoteCommand.ApiError = "CPF2204";
        Assert.False(await SignInIsAdminAsync(factory, "alice"));
        ibm.RemoteCommand.ApiError = null;
        ibm.RemoteCommand.RefuseCall = 0x0500;
        Assert.False(await SignInIsAdminAsync(factory, "alice"));
        ibm.RemoteCommand.RefuseCall = null;
        Assert.True(await SignInIsAdminAsync(factory, "alice"));

        await ibm.RemoteCommand.DisposeAsync();
        Assert.False(await SignInIsAdminAsync(factory, "alice"));
    }

    [Fact]
    public async Task WithoutARuleTheIbmIIsNotAsked()
    {
        await using var ibm = new Ibm();
        ibm.Accounts.Set("ALICE", Password);
        ibm.Accounts.SetProfile("ALICE", group: "GGADMIN");
        using var factory = IbmFactory(ibm, groups: null, authorities: null);
        Assert.False(await SignInIsAdminAsync(factory, "alice"));
        Assert.Equal(0, ibm.RemoteCommand.Calls);

        // An administrator sets one on the admin page.
        var admin = Client(factory, await BreakglassAsync(factory));
        var bad = await admin.PutAsJsonAsync("/api/admin/signin/ibmi-admins", new { groups = "GGADMIN", authorities = "*EVERYTHING" }, T.Token());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("authorities", (await Json(bad)).GetProperty("error").GetString());
        var good = await admin.PutAsJsonAsync("/api/admin/signin/ibmi-admins", new { groups = "ggadmin", authorities = "secadm" }, T.Token());
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("*SECADM", (await Json(good)).GetProperty("authorities")[0].GetString());
        Assert.True(await SignInIsAdminAsync(factory, "alice"));
    }

    [Fact]
    public async Task TheEnvironmentSetsUpLocalAccounts()
    {
        using var factory = Factory(null, ("Setup:Method", "local"), ("Setup:BreakglassPassword", Breakglass),
            ("Setup:Local:User", "admin"), ("Setup:Local:Password", "local admin placeholder"));
        var response = await Client(factory).PostAsJsonAsync("/auth/password",
            new { user = "admin", password = "local admin placeholder" }, T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Json(await Client(factory, Cookie(response)).GetAsync("/api/me", T.Token()))).GetProperty("isAdmin").GetBoolean());
        // The public URL wasn't stored: Server__PublicUrl stays in force.
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        Assert.Null(await db.Settings.FirstOrDefaultAsync(s => s.Key == Web.PublicAddress.Setting));
    }

    [Theory]
    [InlineData("local", null, null, "Setup__BreakglassPassword")]
    [InlineData("ibmi", "nope", null, "Setup__IbmI__Connection")]
    [InlineData("ibmi", "prod", "*EVERYTHING", "Setup__IbmI__AdminAuthorities")]
    [InlineData("sideways", null, null, "Setup__Method")]
    public void AChoiceThatIsRefusedStopsTheStartNamingTheVariable(string method, string? connection, string? authorities, string variable)
    {
        using var factory = Factory(null, ("Setup:Method", method),
            ("Setup:BreakglassPassword", variable == "Setup__BreakglassPassword" ? null : Breakglass),
            ("Setup:IbmI:Connection", connection), ("Setup:IbmI:AdminAuthorities", authorities));
        var error = Assert.ThrowsAny<Exception>(() => factory.Server);
        var message = (error as Configuration.ConfigException ?? error.InnerException as Configuration.ConfigException)?.Message ?? error.Message;
        Assert.StartsWith(variable + ":", message);
    }

    [Fact]
    public async Task ASetUpServerIgnoresTheEnvironment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "greenglow-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = ("Data:DbPath", Path.Combine(directory, "greenglow.db"));
        try
        {
            using (var first = Factory(null, db, ("Setup:Method", "local"), ("Setup:BreakglassPassword", Breakglass)))
                _ = first.Server;
            // Later Setup__* values change nothing: the database decides now.
            using var second = Factory(null, db, ("Setup:Method", "ibmi"), ("Setup:BreakglassPassword", "another placeholder 2"),
                ("Setup:IbmI:Connection", "prod"));
            var method = await Json(await Client(second).GetAsync("/auth/method", T.Token()));
            Assert.Equal("local", method.GetProperty("method").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Client(second).PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token())).StatusCode);
        }
        finally
        {
            T.ClearPool(db.Item2);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("local.user", "Setup__Local__User")]
    [InlineData("ibmi.adminGroups", "Setup__IbmI__AdminGroups")]
    [InlineData("breakglassPassword", "Setup__BreakglassPassword")]
    public void FieldsNameTheirVariables(string field, string variable) => Assert.Equal(variable, SetupOptions.Variable(field));
}
