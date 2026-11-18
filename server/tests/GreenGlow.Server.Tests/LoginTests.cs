using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.FakeHost;
using GreenGlow.Server.Data;
using GreenGlow.Server.Logins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>
/// Saved IBM i logins: kept per user and system, checked by the fake IBM i's sign-on server,
/// and used to sign a session on during negotiation and to sign the printer panel on.
/// Placeholder users and passwords only.
/// </summary>
public sealed class LoginTests
{
    private const string Key = "a placeholder sealing key of 32+ characters";

    /// <summary>The fake IBM i with every server sharing one set of user profiles, and a GreenGlow server on it.</summary>
    private sealed class System : IAsyncDisposable
    {
        public required FakeAccounts Accounts { get; init; }
        public required FakeIbmI Host { get; init; }
        public required FakeSignOn SignOn { get; init; }
        public required FakeNetworkPrint Print { get; init; }
        public required RelayFactory Factory { get; set; }
        public required Dictionary<string, string?> Settings { get; init; }

        public static async Task<System> StartAsync(int level = 2, bool key = false)
        {
            var accounts = new FakeAccounts { PasswordLevel = level };
            accounts.Set("JSMITH", "Passw0rd!");
            accounts.Set("JSMITH2", "Other#Pass9");
            var host = await FakeIbmI.StartAsync();
            host.Accounts = accounts;
            var signOn = new FakeSignOn(accounts);
            var print = await FakeNetworkPrint.StartAsync();
            print.Accounts = accounts;
            var settings = new Dictionary<string, string?>
            {
                ["Server:PublicUrl"] = T.Origin,
                ["Seed:ConnectionsJson"] = RelayFactory.SeedJson($"test=tcp://127.0.0.1:{host.Port}"),
                ["HostServers:SignOnPort"] = signOn.Port.ToString(),
                ["Printer:Port"] = print.Port.ToString(),
                // Sessions stay open with no browser attached, long enough to look at them.
                ["Sessions:IdleMinutes"] = "5",
            };
            if (key) settings["Credentials:Key"] = Key;
            return new System { Accounts = accounts, Host = host, SignOn = signOn, Print = print, Settings = settings, Factory = new RelayFactory(settings) };
        }

        public GreenGlowDb Db() => Factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContext();

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await Host.DisposeAsync();
            await SignOn.DisposeAsync();
            await Print.DisposeAsync();
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(T.Token()) is { Length: > 0 } text ? JsonSerializer.Deserialize<JsonElement>(text) : default;

    private static async Task<JsonElement> SaveAsync(HttpClient client, string user = "jsmith", string password = "Passw0rd!", bool? remember = null)
    {
        var response = await client.PostAsJsonAsync("/api/logins/test", new { user, password, remember }, T.Token());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonElement> SessionAsync(HttpClient client, Guid id, Func<JsonElement, bool> until)
    {
        for (var i = 0; i < 200; i++)
        {
            var sessions = (await Json(await client.GetAsync("/api/sessions", T.Token()))).GetProperty("sessions");
            var session = sessions.EnumerateArray().FirstOrDefault(s => s.GetProperty("id").GetGuid() == id);
            if (session.ValueKind == JsonValueKind.Object && until(session)) return session;
            if (session.ValueKind != JsonValueKind.Object && i > 0) return default;
            await Task.Delay(25, T.Token());
        }
        throw new TimeoutException("the session never got there");
    }

    [Fact]
    public async Task ALoginIsSavedOnlyOnceTheIbmIAcceptsIt()
    {
        await using var system = await System.StartAsync();
        var client = await system.Factory.SignedInClientAsync();

        var wrong = await client.PostAsJsonAsync("/api/logins/test", new { user = "jsmith", password = "not-the-password" }, T.Token());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.Equal("wrong-password", (await Json(wrong)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/logins/test", new { user = "not a profile", password = "x" }, T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/logins/nowhere", new { user = "jsmith", password = "Passw0rd!" }, T.Token())).StatusCode);
        Assert.Empty((await Json(await client.GetAsync("/api/logins/test"))).GetProperty("logins").EnumerateArray());

        // Several logins for the same system, each a user profile; saving one again keeps one row.
        var first = await SaveAsync(client);
        Assert.Equal("JSMITH", first.GetProperty("ibmUser").GetString());
        Assert.True(first.GetProperty("ready").GetBoolean());
        Assert.False(first.GetProperty("remembered").GetBoolean());
        await SaveAsync(client, "JSMITH2", "Other#Pass9");
        await SaveAsync(client);
        var list = await Json(await client.GetAsync("/api/logins/test"));
        Assert.False(list.GetProperty("canRemember").GetBoolean());
        Assert.Equal(["JSMITH", "JSMITH2"], list.GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("ibmUser").GetString()));

        // Without a sealing key nothing of the password is in the database, whatever was asked.
        await SaveAsync(client, remember: true);
        await using var db = system.Db();
        Assert.All(await db.IbmLogins.ToListAsync(), l => Assert.Null(l.SealedPassword));

        // They are the user's own: nobody else sees or uses them.
        var other = await system.Factory.SignedInClientAsync("other@example.com");
        Assert.Empty((await Json(await other.GetAsync("/api/logins/test"))).GetProperty("logins").EnumerateArray());
        var id = first.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/logins/test/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsJsonAsync("/api/sessions", new { target = "test", login = id }, T.Token())).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/logins/test/{id}")).StatusCode);
        Assert.Single((await Json(await client.GetAsync("/api/logins/test"))).GetProperty("logins").EnumerateArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ASessionOpenedWithALoginIsSignedOnWithoutTheSignOnScreen(int level)
    {
        await using var system = await System.StartAsync(level);
        var client = await system.Factory.SignedInClientAsync();
        var login = await SaveAsync(client);

        var opened = await client.PostAsJsonAsync("/api/sessions", new { target = "test", login = login.GetProperty("id").GetGuid() }, T.Token());
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var id = (await Json(opened)).GetProperty("id").GetGuid();
        var session = await SessionAsync(client, id, s => s.GetProperty("signedOn").GetBoolean());
        Assert.Equal("JSMITH", session.GetProperty("ibmUser").GetString());
        Assert.Equal(1, system.Host.AutoSignOns);
        // Nothing was typed for the user: the IBM i got no input at all.
        Assert.Empty(system.Host.Inputs);

        // Without a login the sign-on screen is there as ever.
        var plain = await client.PostAsJsonAsync("/api/sessions", new { target = "test" }, T.Token());
        var plainId = (await Json(plain)).GetProperty("id").GetGuid();
        var unsigned = await SessionAsync(client, plainId, s => s.GetProperty("title").GetString() is { Length: > 0 });
        Assert.False(unsigned.GetProperty("signedOn").GetBoolean());
        Assert.Equal(1, system.Host.AutoSignOns);
    }

    [Fact]
    public async Task ALoginTheIbmIRefusesEndsTheSessionAndIsNeverTriedAgain()
    {
        await using var system = await System.StartAsync();
        var client = await system.Factory.SignedInClientAsync();
        var login = (await SaveAsync(client)).GetProperty("id").GetGuid();
        // The password is changed on the IBM i behind GreenGlow's back.
        system.Accounts.Set("JSMITH", "Changed#Elsewhere1");

        var opened = await client.PostAsJsonAsync("/api/sessions", new { target = "test", login }, T.Token());
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var id = (await Json(opened)).GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Undefined, (await SessionAsync(client, id, _ => false)).ValueKind);
        Assert.Equal(0, system.Host.AutoSignOns);
        await using (var db = system.Db())
        {
            for (var i = 0; i < 100 && !await db.Logs.AnyAsync(l => l.SessionId == id && l.EndReason != null); i++) await Task.Delay(25);
            Assert.Equal("sign-on refused", (await db.Logs.FirstAsync(l => l.SessionId == id && l.Kind == LogKind.Session)).EndReason);
        }

        // The password is gone: the next session asks for it instead of sending it again.
        var list = (await Json(await client.GetAsync("/api/logins/test"))).GetProperty("logins");
        Assert.False(list[0].GetProperty("ready").GetBoolean());
        var again = await client.PostAsJsonAsync("/api/sessions", new { target = "test", login }, T.Token());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("password-needed", (await Json(again)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhereAutomaticSignOnIsNotAllowedTheSignOnScreenFollows()
    {
        await using var system = await System.StartAsync();
        system.Host.RemoteSignOn = false;
        var client = await system.Factory.SignedInClientAsync();
        var login = (await SaveAsync(client)).GetProperty("id").GetGuid();

        var opened = await client.PostAsJsonAsync("/api/sessions", new { target = "test", login }, T.Token());
        var id = (await Json(opened)).GetProperty("id").GetGuid();
        var session = await SessionAsync(client, id, s => s.GetProperty("title").GetString() is { Length: > 0 });
        Assert.False(session.GetProperty("signedOn").GetBoolean());
        // Not the password's fault: the login is still ready.
        Assert.True((await Json(await client.GetAsync("/api/logins/test"))).GetProperty("logins")[0].GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task WithoutTheSignOnServerTheSessionOpensAtTheSignOnScreen()
    {
        await using var system = await System.StartAsync();
        var client = await system.Factory.SignedInClientAsync();
        var login = (await SaveAsync(client)).GetProperty("id").GetGuid();
        await system.SignOn.DisposeAsync();
        // What was learned about the system a moment ago is forgotten, as after ten minutes.
        system.Factory.Services.GetRequiredService<AutoSignOns>().Forget("test");

        var opened = await client.PostAsJsonAsync("/api/sessions", new { target = "test", login }, T.Token());
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var id = (await Json(opened)).GetProperty("id").GetGuid();
        var session = await SessionAsync(client, id, s => s.GetProperty("title").GetString() is { Length: > 0 });
        Assert.False(session.GetProperty("signedOn").GetBoolean());
        Assert.Equal(0, system.Host.AutoSignOns);
    }

    [Fact]
    public async Task ARememberedPasswordIsSealedAndSurvivesARestart()
    {
        await using var system = await System.StartAsync(key: true);
        var dbPath = Path.Combine(Directory.CreateTempSubdirectory("greenglow-logins-").FullName, "greenglow.db");
        system.Settings["Data:DbPath"] = dbPath;
        system.Factory.Dispose();
        system.Factory = new RelayFactory(system.Settings);
        var client = await system.Factory.SignedInClientAsync();

        Assert.True((await Json(await client.GetAsync("/api/logins/test"))).GetProperty("canRemember").GetBoolean());
        var remembered = await SaveAsync(client, remember: true);
        Assert.True(remembered.GetProperty("remembered").GetBoolean());
        await SaveAsync(client, "JSMITH2", "Other#Pass9");
        await using (var db = system.Db())
        {
            var rows = await db.IbmLogins.OrderBy(l => l.IbmUser).ToListAsync();
            Assert.StartsWith("v1:", rows[0].SealedPassword);
            Assert.DoesNotContain("Passw0rd!", rows[0].SealedPassword);
            Assert.Null(rows[1].SealedPassword);
        }

        // A new server on the same database: memory is empty, the sealed password still opens.
        system.Factory.Dispose();
        system.Factory = new RelayFactory(system.Settings);
        client = await system.Factory.SignedInClientAsync();
        var logins = (await Json(await client.GetAsync("/api/logins/test"))).GetProperty("logins");
        Assert.True(logins[0].GetProperty("ready").GetBoolean());
        Assert.False(logins[1].GetProperty("ready").GetBoolean());
        var opened = await client.PostAsJsonAsync("/api/sessions", new { target = "test", login = logins[0].GetProperty("id").GetGuid() }, T.Token());
        var id = (await Json(opened)).GetProperty("id").GetGuid();
        await SessionAsync(client, id, s => s.GetProperty("signedOn").GetBoolean());
        Assert.Equal(1, system.Host.AutoSignOns);

        // An administrator may forbid remembering: what was sealed is no longer offered for new saves.
        var admin = await system.Factory.SignedInClientAsync("admin@example.com");
        await using (var db = system.Db())
        {
            (await db.Users.SingleAsync(u => u.Upn == "admin@example.com")).IsAdmin = true;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/signin/remember", new { allow = false }, T.Token())).StatusCode);
        Assert.False((await Json(await client.GetAsync("/api/logins/test"))).GetProperty("canRemember").GetBoolean());
        Assert.False((await SaveAsync(client, remember: true)).GetProperty("remembered").GetBoolean());
        T.ClearPool(dbPath);
    }

    [Fact]
    public void ASealedPasswordOpensOnlyWithItsKeyAndForItsRow()
    {
        var seal = new CredentialSeal(new CredentialOptions { Key = Key });
        var sealedPassword = seal.Seal("Passw0rd!", "user-1|connection-1|JSMITH");
        Assert.Equal("Passw0rd!", seal.Open(sealedPassword, "user-1|connection-1|JSMITH"));
        Assert.NotEqual(sealedPassword, seal.Seal("Passw0rd!", "user-1|connection-1|JSMITH"));
        Assert.Null(seal.Open(sealedPassword, "user-2|connection-1|JSMITH"));
        Assert.Null(new CredentialSeal(new CredentialOptions { Key = Key + " but another" }).Open(sealedPassword, "user-1|connection-1|JSMITH"));
        Assert.Null(new CredentialSeal(new CredentialOptions()).Open(sealedPassword, "user-1|connection-1|JSMITH"));
        Assert.False(new CredentialSeal(new CredentialOptions()).Available);
        Assert.Throws<Configuration.ConfigException>(() => new CredentialSeal(new CredentialOptions { Key = "too short" }));
    }

    [Fact]
    public async Task ThePrinterPanelSignsOnWithASavedLogin()
    {
        await using var system = await System.StartAsync();
        var client = await system.Factory.SignedInClientAsync();
        var login = (await SaveAsync(client)).GetProperty("id").GetGuid();

        var status = await Json(await client.GetAsync("/api/printer/test"));
        Assert.False(status.GetProperty("signedOn").GetBoolean());
        Assert.True(status.GetProperty("logins")[0].GetProperty("ready").GetBoolean());

        var signedOn = await client.PostAsJsonAsync("/api/printer/test/signon", new { login }, T.Token());
        Assert.Equal(HttpStatusCode.OK, signedOn.StatusCode);
        Assert.Equal("JSMITH", (await Json(signedOn)).GetProperty("ibmUser").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/printer/test/files")).StatusCode);

        // Typing a user and password there can save them as a login too.
        await client.DeleteAsync("/api/printer/test/signon");
        var typed = await client.PostAsJsonAsync("/api/printer/test/signon",
            new { user = "jsmith2", password = "Other#Pass9", save = true }, T.Token());
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        var logins = (await Json(await client.GetAsync("/api/logins/test"))).GetProperty("logins");
        Assert.Equal(["JSMITH", "JSMITH2"], logins.EnumerateArray().Select(l => l.GetProperty("ibmUser").GetString()));

        // A login the IBM i no longer takes is dropped, and the panel asks.
        await client.DeleteAsync("/api/printer/test/signon");
        system.Accounts.Set("JSMITH", "Changed#Elsewhere1");
        var refused = await client.PostAsJsonAsync("/api/printer/test/signon", new { login }, T.Token());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var needed = await client.PostAsJsonAsync("/api/printer/test/signon", new { login }, T.Token());
        Assert.Equal(HttpStatusCode.Conflict, needed.StatusCode);
        Assert.Equal("password-needed", (await Json(needed)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnIbmIPasswordIsChangedFromItsLogin()
    {
        await using var system = await System.StartAsync(level: 3);
        var client = await system.Factory.SignedInClientAsync();
        await SaveAsync(client);
        system.Accounts.Expire("JSMITH");

        var expired = await client.PostAsJsonAsync("/api/logins/test", new { user = "jsmith", password = "Passw0rd!" }, T.Token());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, expired.StatusCode);
        Assert.Equal("password-expired", (await Json(expired)).GetProperty("error").GetString());

        var refused = await client.PostAsJsonAsync("/api/logins/test/change",
            new { user = "jsmith", password = "Passw0rd!", newPassword = "tiny" }, T.Token());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("new-password-refused", (await Json(refused)).GetProperty("error").GetString());

        var changed = await client.PostAsJsonAsync("/api/logins/test/change",
            new { user = "jsmith", password = "Passw0rd!", newPassword = "Renewed#2026" }, T.Token());
        Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
        Assert.Equal("Renewed#2026", system.Accounts.PasswordOf("JSMITH"));

        // The login now holds the new password: a session signs on with it.
        var opened = await client.PostAsJsonAsync("/api/sessions",
            new { target = "test", login = (await Json(changed)).GetProperty("id").GetGuid() }, T.Token());
        var id = (await Json(opened)).GetProperty("id").GetGuid();
        await SessionAsync(client, id, s => s.GetProperty("signedOn").GetBoolean());

        await using var db = system.Db();
        var log = await db.Logs.Where(l => l.Kind == LogKind.SignIn).Select(l => l.EndReason).ToListAsync();
        Assert.Contains("IBM i password changed", log);
        Assert.Contains("login check failed: password expired", log);
    }
}
