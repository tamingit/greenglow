using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.FakeHost;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>
/// First-run setup and the sign-in methods an administrator chooses between: local accounts,
/// IBM i sign-in (against the fake IBM i's sign-on server) and Entra ID, with the breakglass
/// administrator. Placeholder users and passwords only.
/// </summary>
public sealed class SignInMethodTests
{
    private const string Breakglass = "breakglass placeholder 1";
    private const string AdminPassword = "local admin placeholder";

    private static RelayFactory Factory(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Auth:DevLogin"] = "false",
        };
        foreach (var (key, value) in extra) settings[key] = value;
        return new RelayFactory(settings);
    }

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

    private static object LocalSetup() => new
    {
        breakglassPassword = Breakglass,
        method = "local",
        local = new { user = "admin", name = "Local Admin", password = AdminPassword },
    };

    /// <summary>Sets the server up with local accounts; returns the first administrator's cookie.</summary>
    private static async Task<string> SetUpLocalAsync(RelayFactory factory)
    {
        var response = await Client(factory).PostAsJsonAsync("/auth/setup", LocalSetup(), T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Cookie(response)!;
    }

    private static async Task<(HttpStatusCode Status, string? Cookie, JsonElement Body)> SignInAsync(RelayFactory factory, string user,
        string password, string path = "/auth/password", string? newPassword = null)
    {
        var response = await Client(factory).PostAsJsonAsync(path, new { user, password, newPassword }, T.Token());
        return (response.StatusCode, Cookie(response), await Json(response));
    }

    [Fact]
    public async Task ANewServerOnlyOffersSetup()
    {
        using var factory = Factory();
        var client = Client(factory);
        Assert.Equal("/setup.html", (await client.GetAsync("/auth/signin?returnUrl=/admin.html")).Headers.Location?.OriginalString);
        Assert.True((await Json(await client.GetAsync("/auth/setup"))).GetProperty("needed").GetBoolean());
        Assert.Equal("setup", (await Json(await client.GetAsync("/auth/method"))).GetProperty("method").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/auth/password", new { user = "admin", password = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/auth/breakglass", new { password = Breakglass })).StatusCode);
        // The log says how to set it up.
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("is not set up"));
    }

    [Fact]
    public async Task TheSetupPageIsGoneOnceTheServerIsSetUp()
    {
        using var factory = Factory();
        await SetUpLocalAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/setup.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/Setup.html")).StatusCode);
    }

    [Fact]
    public async Task SetupNeedsNoTokenButStillNeedsAnAllowedOrigin()
    {
        using var factory = Factory();
        var client = Client(factory);
        Assert.True((await Json(await client.GetAsync("/auth/setup"))).GetProperty("needed").GetBoolean());

        using var noOrigin = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await noOrigin.PostAsJsonAsync("/auth/setup", LocalSetup(), T.Token())).StatusCode);
    }

    [Theory]
    [InlineData("""{"method":"local","local":{"user":"admin","password":"local admin placeholder"}}""", "breakglassPassword")]
    [InlineData("""{"breakglassPassword":"short","method":"local","local":{"user":"admin","password":"local admin placeholder"}}""", "breakglassPassword")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"ldap"}""", "method")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"local","local":{"user":"","password":"local admin placeholder"}}""", "local.user")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"local","local":{"user":"breakglass","password":"local admin placeholder"}}""", "local.user")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"local","local":{"user":"admin","password":"short"}}""", "local.password")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"entra","entra":{"tenantId":"nope","clientId":"00000000-0000-0000-0000-000000000002","clientSecret":"s"}}""", "entra.tenantId")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"entra","entra":{"tenantId":"00000000-0000-0000-0000-000000000001","clientId":"00000000-0000-0000-0000-000000000002"}}""", "entra.clientSecret")]
    [InlineData("""{"breakglassPassword":"breakglass placeholder 1","method":"ibmi","ibmi":{"connection":"nowhere","user":"PRTUSER","password":"Secret12"}}""", "ibmi.connection")]
    public async Task ASetupWithSomethingWrongSavesNothing(string body, string field)
    {
        using var factory = Factory();
        var client = Client(factory);
        var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
        var refused = await client.PostAsJsonAsync("/auth/setup", request, T.Token());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(field, (await Json(refused)).GetProperty("error").GetString());
        Assert.True((await Json(await client.GetAsync("/auth/setup"))).GetProperty("needed").GetBoolean());
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.Settings.ToListAsync());
    }

    [Fact]
    public async Task SetupWithLocalAccountsMakesTheFirstAdministratorAndIsDoneOnce()
    {
        using var factory = Factory();
        var cookie = await SetUpLocalAsync(factory);

        var me = await Json(await Client(factory, cookie).GetAsync("/api/me"));
        Assert.Equal("admin", me.GetProperty("upn").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("local", me.GetProperty("method").GetString());

        var again = await Client(factory).PostAsJsonAsync("/auth/setup", LocalSetup(), T.Token());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.False((await Json(await Client(factory).GetAsync("/auth/setup"))).GetProperty("needed").GetBoolean());
        Assert.Equal("/signin.html?returnUrl=%2Fadmin.html",
            (await Client(factory).GetAsync("/auth/signin?returnUrl=/admin.html")).Headers.Location?.OriginalString);

        // Passwords are kept hashed, never as typed.
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        var hashes = await db.Users.Select(u => u.PasswordHash).ToListAsync();
        Assert.Equal(2, hashes.Count);
        Assert.All(hashes, h => Assert.StartsWith("pbkdf2-sha256$", h));
        Assert.DoesNotContain(await db.Settings.ToListAsync(), s => s.Value.Contains(Breakglass) || s.Value.Contains(AdminPassword));
    }

    [Fact]
    public async Task ALocalAccountSignsInWithItsPasswordOnly()
    {
        using var factory = Factory();
        await SetUpLocalAsync(factory);

        var wrong = await SignInAsync(factory, "admin", "not the password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Null(wrong.Cookie);
        var unknown = await SignInAsync(factory, "nobody", AdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        // The same words for a wrong name as for a wrong password.
        Assert.Equal(wrong.Body.GetProperty("message").GetString(), unknown.Body.GetProperty("message").GetString());

        var right = await SignInAsync(factory, "ADMIN", AdminPassword);
        Assert.Equal(HttpStatusCode.OK, right.Status);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, right.Cookie).GetAsync("/api/me")).StatusCode);

        // The breakglass password doesn't sign a local account in, nor the reverse.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "breakglass", Breakglass)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(factory).PostAsJsonAsync("/auth/breakglass", new { password = AdminPassword }, T.Token())).StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswordsLockTheNameForAWhile()
    {
        using var factory = Factory();
        await SetUpLocalAsync(factory);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "admin", $"wrong guess {i}")).Status);
        var locked = await SignInAsync(factory, "admin", AdminPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.Status);
        Assert.Null(locked.Cookie);
        Assert.Equal("locked", locked.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnAdministratorAddsAnAccountWhoseOwnerChoosesTheirOwnPassword()
    {
        using var factory = Factory();
        var admin = Client(factory, await SetUpLocalAsync(factory));
        var created = await admin.PostAsJsonAsync("/api/admin/users",
            new { user = "pat", name = "Pat Example", password = "first password 123" }, T.Token());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/users",
            new { user = "PAT", password = "first password 123" }, T.Token())).StatusCode);

        var first = await SignInAsync(factory, "pat", "first password 123");
        Assert.Equal(HttpStatusCode.Conflict, first.Status);
        Assert.Equal("password-change-required", first.Body.GetProperty("error").GetString());
        Assert.Null(first.Cookie);

        var same = await SignInAsync(factory, "pat", "first password 123", "/auth/password/change", "first password 123");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, same.Status);
        var weak = await SignInAsync(factory, "pat", "first password 123", "/auth/password/change", "short");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.Status);
        var changed = await SignInAsync(factory, "pat", "first password 123", "/auth/password/change", "my own password 456");
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        var pat = Client(factory, changed.Cookie);
        var me = await Json(await pat.GetAsync("/api/me"));
        Assert.Equal("Pat Example", me.GetProperty("name").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await pat.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "pat", "first password 123")).Status);

        // Switched off: signed out at once, and no way back in.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/users/{id}", new { disabled = true }, T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pat.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "pat", "my own password 456")).Status);

        // The list never carries a hash.
        var list = await (await admin.GetAsync("/api/admin/users")).Content.ReadAsStringAsync(T.Token());
        Assert.Contains("\"pat\"", list);
        Assert.DoesNotContain("pbkdf2", list);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{id}")).StatusCode);
        Assert.DoesNotContain("\"pat\"", await (await admin.GetAsync("/api/admin/users")).Content.ReadAsStringAsync(T.Token()));
    }

    [Fact]
    public async Task AnAdministratorCannotLockThemselvesOrEveryoneOut()
    {
        using var factory = Factory();
        var admin = Client(factory, await SetUpLocalAsync(factory));
        var users = (await Json(await admin.GetAsync("/api/admin/users"))).GetProperty("users").EnumerateArray().ToList();
        var me = users.Single(u => u.GetProperty("user").GetString() == "admin").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/users/{me}", new { disabled = true }, T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/users/{me}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/admin/users/breakglass")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/admin/admins/breakglass")).StatusCode);
    }

    [Fact]
    public async Task TheBreakglassAdministratorSignsInAndEveryTryIsLogged()
    {
        using var factory = Factory();
        await SetUpLocalAsync(factory);
        var client = Client(factory);
        Assert.True((await Json(await client.GetAsync("/auth/method"))).GetProperty("breakglass").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/auth/breakglass", new { password = "not the breakglass one" }, T.Token())).StatusCode);
        var response = await client.PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var breakglass = Client(factory, Cookie(response));
        var me = await Json(await breakglass.GetAsync("/api/me"));
        Assert.Equal("breakglass", me.GetProperty("upn").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());

        var log = (await Json(await breakglass.GetAsync("/api/admin/log?kind=signin"))).ToString();
        Assert.Contains("sign-in failed: wrong password", log);
        Assert.Contains("signed in", log);
        Assert.Contains("first-run setup: local", log);
        Assert.DoesNotContain(Breakglass, log);

        // A new breakglass password replaces the old one.
        Assert.Equal(HttpStatusCode.NoContent,
            (await breakglass.PutAsJsonAsync("/api/admin/signin/breakglass", new { password = "another breakglass one" }, T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/auth/breakglass", new { password = "another breakglass one" }, T.Token())).StatusCode);
    }

    // ---- IBM i sign-in ----

    private static object IbmSetup(string user = "PRTUSER", string password = "Secret12", bool usePortMapper = false) => new
    {
        breakglassPassword = Breakglass,
        method = "ibmi",
        ibmi = new
        {
            newConnection = new { name = "prod", label = "Production", url = "tcp://127.0.0.1:23", usePortMapper },
            user,
            password,
        },
    };

    [Fact]
    public async Task SetupWithIbmISignInChecksTheUserOnThatSystemFirst()
    {
        await using var fake = new FakeSignOn(new FakeAccounts());
        using var factory = Factory(("HostServers:SignOnPort", fake.Port.ToString()));
        var client = Client(factory);

        var wrong = await client.PostAsJsonAsync("/auth/setup", IbmSetup(password: "not-the-password"), T.Token());
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("ibmi.user", (await Json(wrong)).GetProperty("error").GetString());
        Assert.True((await Json(await client.GetAsync("/auth/setup"))).GetProperty("needed").GetBoolean());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync())
            Assert.Empty(await db.Connections.ToListAsync());

        var done = await client.PostAsJsonAsync("/auth/setup", IbmSetup(user: "prtuser"), T.Token());
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var admin = Client(factory, Cookie(done));
        var me = await Json(await admin.GetAsync("/api/me"));
        Assert.Equal("PRTUSER", me.GetProperty("upn").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("ibmi", me.GetProperty("method").GetString());

        var method = await Json(await client.GetAsync("/auth/method"));
        Assert.Equal("ibmi", method.GetProperty("method").GetString());
        Assert.Equal("Production", method.GetProperty("system").GetString());

        // The new connection is open to everyone who can sign in with that IBM i.
        var connection = (await Json(await admin.GetAsync("/api/admin/connections"))).GetProperty("connections")[0];
        Assert.Equal("prod", connection.GetProperty("name").GetString());
        Assert.Equal(DatabaseDirectory.EveryoneId, connection.GetProperty("grants")[0].GetProperty("principalId").GetString());
    }

    [Fact]
    public async Task AnIbmIUserSignsInWithTheirIbmIPassword()
    {
        var accounts = new FakeAccounts();
        accounts.Set("JSMITH", "Passw0rd!");
        accounts.Set("LOCKED", "Passw0rd!");
        accounts.Disable("LOCKED");
        await using var fake = new FakeSignOn(accounts);
        using var factory = Factory(("HostServers:SignOnPort", fake.Port.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await Client(factory).PostAsJsonAsync("/auth/setup", IbmSetup(), T.Token())).StatusCode);

        var wrong = await SignInAsync(factory, "jsmith", "not-the-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        var unknown = await SignInAsync(factory, "nobody", "Passw0rd!");
        Assert.Equal(wrong.Body.GetProperty("message").GetString(), unknown.Body.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "locked", "Passw0rd!")).Status);
        // A name that isn't an IBM i user profile never reaches the IBM i.
        var refusals = fake.Refusals;
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "not a profile", "Passw0rd!")).Status);
        Assert.Equal(refusals, fake.Refusals);

        var right = await SignInAsync(factory, "jsmith", "Passw0rd!");
        Assert.Equal(HttpStatusCode.OK, right.Status);
        var user = Client(factory, right.Cookie);
        var me = await Json(await user.GetAsync("/api/me"));
        Assert.Equal("JSMITH", me.GetProperty("upn").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("prod", (await Json(await user.GetAsync("/api/targets"))).GetProperty("targets")[0].GetProperty("name").GetString());

        // The password is nowhere in the database; the login it made is.
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        var login = await db.IbmLogins.SingleAsync(l => l.IbmUser == "JSMITH");
        Assert.Null(login.SealedPassword);
        Assert.Null((await db.Users.SingleAsync(u => u.Upn == "JSMITH")).PasswordHash);
        var log = await db.Logs.Where(l => l.Kind == LogKind.SignIn).ToListAsync();
        Assert.Contains(log, l => l.Upn == "JSMITH" && l.EndReason == "signed in" && l.ConnectionName == "prod");
        Assert.Contains(log, l => l.Upn == "JSMITH" && l.EndReason == "sign-in failed: wrong password");
        Assert.Contains(log, l => l.Upn == "LOCKED" && l.EndReason == "sign-in failed: profile disabled");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AnExpiredIbmIPasswordIsChangedAtSignIn(int level)
    {
        var accounts = new FakeAccounts { PasswordLevel = level };
        accounts.Set("JSMITH", "Passw0rd!");
        await using var fake = new FakeSignOn(accounts);
        using var factory = Factory(("HostServers:SignOnPort", fake.Port.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await Client(factory).PostAsJsonAsync("/auth/setup", IbmSetup(), T.Token())).StatusCode);
        accounts.Expire("JSMITH");

        var expired = await SignInAsync(factory, "jsmith", "Passw0rd!");
        Assert.Equal(HttpStatusCode.Conflict, expired.Status);
        Assert.Equal("password-expired", expired.Body.GetProperty("error").GetString());

        var refused = await SignInAsync(factory, "jsmith", "Passw0rd!", "/auth/password/change", "tiny");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("new-password-refused", refused.Body.GetProperty("error").GetString());

        var changed = await SignInAsync(factory, "jsmith", "Passw0rd!", "/auth/password/change", "Renewed#2026");
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        Assert.Equal("Renewed#2026", accounts.PasswordOf("JSMITH"));
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, changed.Cookie).GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(factory, "jsmith", "Renewed#2026")).Status);
    }

    [Fact]
    public async Task IbmISignInFindsTheSignOnServerThroughThePortMapper()
    {
        await using var fake = new FakeSignOn(new FakeAccounts());
        await using var mapper = new FakePortMapper().Map("as-signon", fake.Port);
        using var factory = Factory(("HostServers:MapperPort", mapper.Port.ToString()));
        var done = await Client(factory).PostAsJsonAsync("/auth/setup", IbmSetup(usePortMapper: true), T.Token());
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(factory, "PRTUSER", "Secret12")).Status);
        Assert.Equal(1, mapper.Lookups);
        var connection = (await Json(await Client(factory, Cookie(done)).GetAsync("/api/admin/connections"))).GetProperty("connections")[0];
        Assert.True(connection.GetProperty("usePortMapper").GetBoolean());
    }

    [Fact]
    public async Task WhenTheIbmIIsDownTheBreakglassAdministratorStillGetsIn()
    {
        int port;
        await using (var fake = new FakeSignOn(new FakeAccounts()))
        {
            port = fake.Port;
            using var setup = Factory(("HostServers:SignOnPort", port.ToString()));
            Assert.Equal(HttpStatusCode.OK, (await Client(setup).PostAsJsonAsync("/auth/setup", IbmSetup(), T.Token())).StatusCode);
            await fake.DisposeAsync();

            var down = await SignInAsync(setup, "PRTUSER", "Secret12");
            Assert.Equal(HttpStatusCode.BadGateway, down.Status);
            var response = await Client(setup).PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Client(setup, Cookie(response)).GetAsync("/api/admin/signin")).StatusCode);
        }
    }

    // ---- Entra ID, and changing the method ----

    private static object EntraSetup() => new
    {
        breakglassPassword = Breakglass,
        method = "entra",
        entra = new
        {
            tenantId = "00000000-0000-0000-0000-000000000001",
            clientId = "00000000-0000-0000-0000-000000000002",
            clientSecret = "placeholder-client-secret",
        },
    };

    [Fact]
    public async Task SetupWithEntraIdKeepsTheSecretSealedAndSignsTheBreakglassAdministratorIn()
    {
        using var factory = Factory();
        var done = await Client(factory).PostAsJsonAsync("/auth/setup", EntraSetup(), T.Token());
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var admin = Client(factory, Cookie(done));
        Assert.Equal("breakglass", (await Json(await admin.GetAsync("/api/me"))).GetProperty("upn").GetString());

        var text = await (await admin.GetAsync("/api/admin/signin")).Content.ReadAsStringAsync(T.Token());
        var signin = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("entra", signin.GetProperty("method").GetString());
        Assert.False(signin.GetProperty("fromEnvironment").GetBoolean());
        Assert.True(signin.GetProperty("entra").GetProperty("hasSecret").GetBoolean());
        Assert.DoesNotContain("placeholder-client-secret", text);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync())
            Assert.DoesNotContain(await db.Settings.ToListAsync(), s => s.Value.Contains("placeholder-client-secret"));

        // Password sign-in is off; the breakglass page isn't.
        Assert.Equal(HttpStatusCode.NotFound, (await SignInAsync(factory, "admin", AdminPassword)).Status);
        Assert.Equal(HttpStatusCode.OK,
            (await Client(factory).PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token())).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorChangesTheMethodAndOldAccountsStopWorking()
    {
        await using var fake = new FakeSignOn(new FakeAccounts());
        using var factory = Factory(("HostServers:SignOnPort", fake.Port.ToString()));
        var localCookie = await SetUpLocalAsync(factory);
        var admin = Client(factory, localCookie);

        // The IBM i user must check out before anything changes.
        var refused = await admin.PutAsJsonAsync("/api/admin/signin", new
        {
            method = "ibmi",
            ibmi = new { newConnection = new { name = "prod", url = "tcp://127.0.0.1:23" }, user = "PRTUSER", password = "wrong" },
        }, T.Token());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("local", (await Json(await admin.GetAsync("/api/admin/signin"))).GetProperty("method").GetString());

        var changed = await admin.PutAsJsonAsync("/api/admin/signin", new
        {
            method = "ibmi",
            ibmi = new { newConnection = new { name = "prod", url = "tcp://127.0.0.1:23" }, user = "PRTUSER", password = "Secret12" },
        }, T.Token());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await Json(changed);
        Assert.False(body.GetProperty("signedOut").GetBoolean());
        Assert.Equal("ibmi", body.GetProperty("signin").GetProperty("method").GetString());

        // The caller carries on as the IBM i administrator; the local account's cookie and password are no use now.
        Assert.Equal("PRTUSER", (await Json(await Client(factory, Cookie(changed)).GetAsync("/api/me"))).GetProperty("upn").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, "admin", AdminPassword)).Status);
        Assert.Equal(HttpStatusCode.OK,
            (await Client(factory).PostAsJsonAsync("/auth/breakglass", new { password = Breakglass }, T.Token())).StatusCode);
    }

    [Fact]
    public async Task PasswordHashesVerifyOnlyTheirOwnPassword()
    {
        var hash = Auth.PasswordHash.Create("a placeholder password");
        Assert.True(Auth.PasswordHash.Verify(hash, "a placeholder password"));
        Assert.False(Auth.PasswordHash.Verify(hash, "A placeholder password"));
        Assert.False(Auth.PasswordHash.Verify(null, "a placeholder password"));
        Assert.False(Auth.PasswordHash.Verify("not a hash", "a placeholder password"));
        Assert.NotEqual(hash, Auth.PasswordHash.Create("a placeholder password"));
        await Task.CompletedTask;
    }
}
