using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GreenGlow.FakeHost;
using GreenGlow.Server.Data;
using GreenGlow.Server.Printing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

/// <summary>/api/printer: print sessions against the fake Network Print server.</summary>
public sealed class PrinterTests
{
    private const string Url = "/api/printer/test";

    private static RelayFactory Factory(FakeNetworkPrint fake, FakeTimeProvider? clock = null)
    {
        var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            // The Telnet address isn't used; the print server is the fake's port.
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
            ["Printer:Port"] = fake.Port.ToString(),
        });
        if (clock is not null) factory.ExtraServices = s => s.AddSingleton<TimeProvider>(clock);
        return factory;
    }

    private static Task<HttpResponseMessage> SignOnAsync(HttpClient client, string password = FakeNetworkPrint.DefaultPassword) =>
        client.PostAsJsonAsync($"{Url}/signon", new { user = "prtuser", password });

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<List<ConnectionLog>> PrinterRowsAsync(RelayFactory factory)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        return await db.Logs.AsNoTracking().Where(r => r.Kind == LogKind.Printer).OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task SignsOnListsOpensAPdfAndSignsOff()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();

        var state = await Json(await client.GetAsync(Url));
        Assert.False(state.GetProperty("signedOn").GetBoolean());

        var signedOn = await Json(await SignOnAsync(client));
        Assert.Equal("PRTUSER", signedOn.GetProperty("ibmUser").GetString());
        Assert.True((await Json(await client.GetAsync(Url))).GetProperty("signedOn").GetBoolean());

        var list = await Json(await client.GetAsync($"{Url}/files"));
        var files = list.GetProperty("files").EnumerateArray().ToList();
        // Newest first.
        Assert.Equal(["QPDFOUT", "QPRTAFP", "QSYSPRT"], files.Select(f => f.GetProperty("name").GetString()));
        Assert.All(files, f => Assert.Matches("^[0-9a-f]{24}$", f.GetProperty("id").GetString()));
        var report = files.Single(f => f.GetProperty("name").GetString() == "QSYSPRT");
        Assert.Equal("123456/PRTUSER/QPADEV0001", report.GetProperty("job").GetString());
        Assert.Equal("2026-10-12T09:30:00", report.GetProperty("created").GetString());
        Assert.Equal("*SCS", report.GetProperty("type").GetString());
        Assert.True(report.GetProperty("canOpen").GetBoolean());
        Assert.False(files.Single(f => f.GetProperty("type").GetString() == "*AFPDS").GetProperty("canOpen").GetBoolean());

        // The same file keeps its id from one list to the next.
        var again = (await Json(await client.GetAsync($"{Url}/files"))).GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "QSYSPRT");
        Assert.Equal(report.GetProperty("id").GetString(), again.GetProperty("id").GetString());

        var pdf = await client.GetAsync($"{Url}/files/{report.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", pdf.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("no-store", pdf.Headers.CacheControl?.ToString());
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync())[..5]);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Url}/signon")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"{Url}/files")).StatusCode);

        var row = Assert.Single(await PrinterRowsAsync(factory));
        Assert.Equal(("PRTUSER", 1, "signed off"), (row.IbmUser, row.FilesOpened, row.EndReason));
        Assert.Equal(("tester@example.com", "test"), (row.Upn, row.ConnectionName));
        Assert.NotNull(row.EndedAt);
    }

    [Fact]
    public async Task FilesThatCantBeShownOrArentListedAreRefused()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();
        await Json(await SignOnAsync(client));
        var files = (await Json(await client.GetAsync($"{Url}/files"))).GetProperty("files").EnumerateArray().ToList();

        var afp = files.Single(f => f.GetProperty("type").GetString() == "*AFPDS").GetProperty("id").GetString();
        var refused = await client.GetAsync($"{Url}/files/{afp}");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
        Assert.Contains("ACS", (await Json(new HttpResponseMessage(HttpStatusCode.OK) { Content = refused.Content })).GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/files/{new string('0', 24)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/files/not-an-id")).StatusCode);

        // The PDF spooled file passes through unchanged.
        var pdf = files.Single(f => f.GetProperty("type").GetString() == "*USERASCII").GetProperty("id").GetString();
        var bytes = await (await client.GetAsync($"{Url}/files/{pdf}")).Content.ReadAsByteArrayAsync();
        Assert.Equal(fake.Files.Single(f => f.Name == "QPDFOUT").Data, bytes);
    }

    [Fact]
    public async Task AWrongPasswordIsSaidSoAndGoesNowhere()
    {
        const string wrong = "Wr0ngSecret";
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();

        var response = await SignOnAsync(client, wrong);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("wrong-password", body);
        Assert.DoesNotContain(wrong, body);
        Assert.Equal(1, fake.Refusals);

        var row = Assert.Single(await PrinterRowsAsync(factory));
        Assert.Equal("sign-on failed: wrong password", row.EndReason);
        Assert.NotNull(row.EndedAt);

        // Nor does the right one, once signed on.
        await Json(await SignOnAsync(client));
        Assert.All(factory.Logs.Entries, e =>
        {
            Assert.DoesNotContain(wrong, e.Message + e.State);
            Assert.DoesNotContain(FakeNetworkPrint.DefaultPassword, e.Message + e.State);
        });
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        var text = string.Join("\n", (await db.Logs.AsNoTracking().ToListAsync()).Select(r => JsonSerializer.Serialize(r)));
        Assert.DoesNotContain(wrong, text);
        Assert.DoesNotContain(FakeNetworkPrint.DefaultPassword, text);
    }

    [Fact]
    public async Task WhatCantBeSentIsABadRequestAndNotLogged()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();
        var response = await client.PostAsJsonAsync($"{Url}/signon", new { user = "not a user", password = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await PrinterRowsAsync(factory));
    }

    [Fact]
    public async Task OnlyTheUsersOwnSystemsAndAnAllowedOrigin()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/printer/nosuch")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/printer/nosuch/signon", new { user = "prtuser", password = "x" })).StatusCode);

        var noOrigin = factory.CreateClient();
        noOrigin.DefaultRequestHeaders.Add("Cookie", await factory.SignInAsync());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noOrigin.PostAsJsonAsync($"{Url}/signon", new { user = "prtuser", password = FakeNetworkPrint.DefaultPassword })).StatusCode);
        Assert.Equal(0, fake.SignOns);

        // Each user has their own print session.
        await Json(await SignOnAsync(client));
        var other = await factory.SignedInClientAsync("someone.else@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await other.GetAsync($"{Url}/files")).StatusCode);
    }

    [Fact]
    public async Task SigningOnAgainEndsTheEarlierSession()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();
        await Json(await SignOnAsync(client));
        await Json(await SignOnAsync(client));
        var rows = await PrinterRowsAsync(factory);
        Assert.Equal(2, rows.Count);
        Assert.Equal("signed on again", rows[0].EndReason);
        Assert.Null(rows[1].EndedAt);
    }

    [Fact]
    public async Task ASessionUnusedForTheIdleTimeEnds()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake, clock);
        var client = await factory.SignedInClientAsync();
        await Json(await SignOnAsync(client));

        clock.Advance(TimeSpan.FromMinutes(14));
        await Json(await client.GetAsync($"{Url}/files"));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True((await Json(await client.GetAsync(Url))).GetProperty("signedOn").GetBoolean());

        clock.Advance(TimeSpan.FromMinutes(16));
        await Wait.UntilAsync(async () => (await PrinterRowsAsync(factory)).Single().EndReason == "idle", "the idle session to end");
        Assert.False((await Json(await client.GetAsync(Url))).GetProperty("signedOn").GetBoolean());
    }

    [Fact]
    public async Task ALostConnectionEndsTheSessionAndAsksToSignOnAgain()
    {
        var fake = await FakeNetworkPrint.StartAsync();
        using var factory = Factory(fake);
        var client = await factory.SignedInClientAsync();
        await Json(await SignOnAsync(client));
        await fake.DisposeAsync();

        var response = await client.GetAsync($"{Url}/files");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("signon-needed", await response.Content.ReadAsStringAsync());
        Assert.Equal("connection lost", (await PrinterRowsAsync(factory)).Single().EndReason);
    }

    [Fact]
    public async Task TheAdminLogShowsPrintSessions()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Seed:ConnectionsJson"] = RelayFactory.SeedJson("test=tcp://127.0.0.1:1"),
            ["Printer:Port"] = fake.Port.ToString(),
            ["Entra:InitialAdminUpn"] = "boss@example.com",
        });
        var client = await factory.SignedInClientAsync("boss@example.com");
        await Json(await SignOnAsync(client));

        var rows = (await Json(await client.GetAsync("/api/admin/log?kind=printer"))).GetProperty("rows").EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal("printer", row.GetProperty("kind").GetString());
        Assert.Equal("PRTUSER", row.GetProperty("ibmUser").GetString());
        Assert.Equal(0, row.GetProperty("filesOpened").GetInt32());
        Assert.Single((await Json(await client.GetAsync("/api/admin/log?q=prtuser"))).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task ThePrintServerIsTheConnectionsHostOnTheDefaultPorts()
    {
        await using var prints = new PrintSessions(new Relay.HostConnector(null), null!, TimeProvider.System,
            new Configuration.PrinterOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PrintSessions>.Instance);
        var secure = new Configuration.Target("prod", "Production", true, "ibmi.example", 992);
        Assert.Equal(9474, prints.EndpointFor(secure).Port);
        Assert.Equal(8474, prints.EndpointFor(secure with { Secure = false, Port = 23 }).Port);
    }
}
