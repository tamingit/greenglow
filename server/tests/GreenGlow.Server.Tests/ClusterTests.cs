using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GreenGlow.Server.Cluster;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>GreenGlow to GreenGlow sync, draining and the peer link.</summary>
public sealed class ClusterTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> ConnectionsAsync(HttpClient admin) =>
        (await JsonAsync(await admin.GetAsync("/api/admin/connections"))).GetProperty("connections").EnumerateArray().ToList();

    [Fact]
    public async Task AConnectionMadeChangedAndDeletedOnOneNodeIsTheSameOnTheOther()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var one = await cluster.AdminAsync(1);
        var two = await cluster.AdminAsync(2);

        var made = await JsonAsync(await one.PostAsJsonAsync("/api/admin/connections", new { name = "prod", url = "tls://prod.example.com:992" }));
        var id = made.GetProperty("id").GetGuid();
        await one.PostAsJsonAsync($"/api/admin/connections/{id}/grants", new { type = "group", id = Guid.NewGuid().ToString(), name = "Ops" });
        await cluster.SettleAsync();
        var there = (await ConnectionsAsync(two)).Single(c => c.GetProperty("name").GetString() == "prod");
        Assert.Equal(id, there.GetProperty("id").GetGuid());
        Assert.Equal("Ops", there.GetProperty("grants").EnumerateArray().Single().GetProperty("name").GetString());

        await two.PutAsJsonAsync($"/api/admin/connections/{id}", new { label = "Production" });
        await cluster.SettleAsync();
        Assert.Equal("Production", (await ConnectionsAsync(one)).Single(c => c.GetProperty("name").GetString() == "prod")
            .GetProperty("label").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await one.DeleteAsync($"/api/admin/connections/{id}")).StatusCode);
        await cluster.SettleAsync();
        Assert.DoesNotContain(await ConnectionsAsync(two), c => c.GetProperty("name").GetString() == "prod");
        await using var db = cluster.Db(2);
        Assert.False(await db.Grants.AnyAsync(g => g.ConnectionId == id));
    }

    [Fact]
    public async Task SeededConnectionsAreTheSameRowOnEveryNode()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var ids = new List<Guid>();
        foreach (var node in new[] { 1, 2 })
            ids.Add((await ConnectionsAsync(await cluster.AdminAsync(node))).Single().GetProperty("id").GetGuid());
        Assert.Equal(ids[0], ids[1]);
    }

    [Fact]
    public async Task ASettingSavedOnOneNodeTakesEffectOnTheOther()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var two = await cluster.AdminAsync(2);
        // Read once, so node 2 has the value cached.
        await two.GetStringAsync("/api/admin/settings");
        await (await cluster.AdminAsync(1)).PutAsJsonAsync("/api/admin/settings/Sessions.MaxFrontends", new { value = 7 });
        await cluster.SettleAsync();
        var settings = (await JsonAsync(await two.GetAsync("/api/admin/settings"))).GetProperty("settings").EnumerateArray();
        Assert.Equal(7, settings.Single(s => s.GetProperty("key").GetString() == "Sessions.MaxFrontends").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task MacrosTheirSharesAndKeysFollowTheirOwnerAndADeleteTakesThemAll()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var pat = await cluster[1].SignedInClientAsync("pat@example.com");
        var made = await JsonAsync(await pat.PostAsJsonAsync("/api/macros", new { name = "Orders" }));
        var id = made.GetProperty("macro").GetProperty("id").GetGuid();
        await pat.PostAsJsonAsync($"/api/macros/{id}/shares", new { type = "user", upn = "sam@example.com" });
        await pat.PutAsJsonAsync($"/api/macros/{id}/keys", new { keys = "Alt+Shift+1" });
        await cluster.SettleAsync();

        var patThere = await cluster[2].SignedInClientAsync("pat@example.com");
        var macro = (await JsonAsync(await patThere.GetAsync("/api/macros"))).GetProperty("macros").EnumerateArray().Single();
        Assert.Equal((id, "Alt+Shift+1", 1), (macro.GetProperty("id").GetGuid(), macro.GetProperty("keys").GetString(),
            macro.GetProperty("sharedWith").GetInt32()));
        var sam = await cluster[2].SignedInClientAsync("sam@example.com");
        Assert.Single((await JsonAsync(await sam.GetAsync("/api/macros"))).GetProperty("macros").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await patThere.DeleteAsync($"/api/macros/{id}")).StatusCode);
        await cluster.SettleAsync();
        await using var db = cluster.Db(1);
        Assert.Empty(await db.Macros.ToListAsync());
        Assert.Empty(await db.MacroShares.ToListAsync());
        Assert.Empty(await db.MacroKeys.ToListAsync());
    }

    [Fact]
    public async Task WritingARowWhileAPeersCopyOfItIsAppliedNeverFails()
    {
        // Both nodes save the same setting over and over while each pulls the other's: a save
        // and an apply must never both make the row's first version.
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var one = await cluster.AdminAsync(1);
        var two = await cluster.AdminAsync(2);
        var writes = Enumerable.Range(1, 20).SelectMany(i => new[]
        {
            one.PutAsJsonAsync("/api/admin/settings/Sessions.IdleMinutes", new { value = i }),
            two.PutAsJsonAsync("/api/admin/settings/Sessions.IdleMinutes", new { value = 100 + i }),
        }).ToList();
        var pulls = Enumerable.Range(0, 5).Select(_ => cluster.SettleAsync()).ToList();
        foreach (var response in await Task.WhenAll(writes)) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Task.WhenAll(pulls);
        await cluster.SettleAsync();
        await using var a = cluster.Db(1);
        await using var b = cluster.Db(2);
        Assert.Equal((await a.Settings.SingleAsync()).Value, (await b.Settings.SingleAsync()).Value);
    }

    [Fact]
    public async Task APokeBringsAChangeOverWithoutWaitingForThePull()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var two = await cluster.AdminAsync(2);
        await (await cluster.AdminAsync(1)).PostAsJsonAsync("/api/admin/connections", new { name = "poked", url = "tcp://h.example.com:23" });
        await Wait.UntilAsync(async () => (await ConnectionsAsync(two)).Any(c => c.GetProperty("name").GetString() == "poked"),
            "the poke to bring the connection over");
    }

    [Fact]
    public async Task ASignInOnOneNodeIsReadByTheOther()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        var cookie = await cluster[1].SignInAsync("pat@example.com");
        // Node 2 starts after node 1, so it takes node 1's sign-in keys (and the user) at once.
        cluster.Start(2);
        var client = cluster[2].CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal("pat@example.com", (await JsonAsync(await client.GetAsync("/api/me"))).GetProperty("upn").GetString());
        await using var one = cluster.Db(1);
        await using var two = cluster.Db(2);
        Assert.Equal((await one.SigningKeys.Select(k => k.Name).OrderBy(n => n).ToListAsync()),
            await two.SigningKeys.Select(k => k.Name).OrderBy(n => n).ToListAsync());
    }

    [Fact]
    public async Task ANodeThatWasDownCatchesUp()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        cluster.Down(2);
        var one = await cluster.AdminAsync(1);
        await one.PostAsJsonAsync("/api/admin/connections", new { name = "while-down", url = "tcp://h.example.com:23" });
        await cluster.Sync(1).PullNowAsync();
        Assert.NotNull(cluster.Sync(1).Peers[2].LastError);
        cluster.Up(2);
        await cluster.SettleAsync();
        Assert.Contains(await ConnectionsAsync(await cluster.AdminAsync(2)), c => c.GetProperty("name").GetString() == "while-down");
        Assert.Null(cluster.Sync(1).Peers[2].LastError);
    }

    [Fact]
    public async Task TwoNodesMakingTheSameNameKeepTheNewerRowOnBoth()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        cluster.Down(1);
        cluster.Down(2);
        var older = await JsonAsync(await (await cluster.AdminAsync(1)).PostAsJsonAsync("/api/admin/connections",
            new { name = "dup", url = "tcp://one.example.com:23" }));
        await Task.Delay(5);
        var newer = await JsonAsync(await (await cluster.AdminAsync(2)).PostAsJsonAsync("/api/admin/connections",
            new { name = "dup", url = "tcp://two.example.com:23" }));
        Assert.NotEqual(older.GetProperty("id").GetGuid(), newer.GetProperty("id").GetGuid());
        cluster.Up(1);
        cluster.Up(2);
        await cluster.SettleAsync();
        foreach (var node in new[] { 1, 2 })
        {
            var dup = (await ConnectionsAsync(await cluster.AdminAsync(node))).Single(c => c.GetProperty("name").GetString() == "dup");
            Assert.Equal(newer.GetProperty("id").GetGuid(), dup.GetProperty("id").GetGuid());
            Assert.Equal("tcp://two.example.com:23", dup.GetProperty("url").GetString());
        }
    }

    [Fact]
    public async Task ConnectionLogRowsReachTheOtherNodeWithTheirNode()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var one = await cluster.AdminAsync(1);
        Assert.Equal(HttpStatusCode.NoContent, (await one.PostAsJsonAsync("/api/admin/nodes/1/drain", new { drain = true })).StatusCode);
        await cluster.SettleAsync();
        var rows = (await JsonAsync(await (await cluster.AdminAsync(2)).GetAsync("/api/admin/log?kind=node")))
            .GetProperty("rows").EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal((1, "drained node 1", TestCluster.Admin),
            (row.GetProperty("node").GetInt32(), row.GetProperty("endReason").GetString(), row.GetProperty("upn").GetString()));
    }

    [Fact]
    public async Task AnAdministratorDrainsAndResumesAnotherNode()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var one = await cluster.AdminAsync(1);
        Assert.Equal(HttpStatusCode.NoContent, (await one.PostAsJsonAsync("/api/admin/nodes/2/drain", new { drain = true })).StatusCode);
        Assert.True(cluster[2].Services.GetRequiredService<ClusterNode>().Draining);

        var nodes = (await JsonAsync(await one.GetAsync("/api/admin/nodes"))).GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["active", "draining"], nodes.Select(n => n.GetProperty("state").GetString()));
        Assert.Equal([true, false], nodes.Select(n => n.GetProperty("self").GetBoolean()));
        Assert.Equal("drain\n", AgentCheck.Answer(cluster[2].Services.GetRequiredService<ClusterNode>().Draining));

        var pat = await cluster[2].SignedInClientAsync("pat@example.com");
        var refused = await pat.PostAsJsonAsync("/api/sessions", new { target = "test" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        await using (var db = cluster.Db(2))
            Assert.Equal("drained node 2 (asked through node 1)", (await db.Logs.SingleAsync(l => l.Kind == LogKind.Node)).EndReason);

        Assert.Equal(HttpStatusCode.NoContent, (await one.PostAsJsonAsync("/api/admin/nodes/2/drain", new { drain = false })).StatusCode);
        Assert.False(cluster[2].Services.GetRequiredService<ClusterNode>().Draining);
    }

    [Fact]
    public async Task TheNodesTabShowsANodeThatIsDownAndItsSyncError()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        var nodes = (await JsonAsync(await (await cluster.AdminAsync(1)).GetAsync("/api/admin/nodes"))).GetProperty("nodes")
            .EnumerateArray().ToList();
        Assert.Equal("unreachable", nodes[1].GetProperty("state").GetString());
        Assert.Contains("down", nodes[1].GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadGateway,
            (await (await cluster.AdminAsync(1)).PostAsJsonAsync("/api/admin/nodes/2/drain", new { drain = true })).StatusCode);
    }

    [Fact]
    public async Task ResyncFromScratchRebuildsANodeFromItsPeers()
    {
        await using var cluster = new TestCluster();
        cluster.Start(1);
        cluster.Start(2);
        var one = await cluster.AdminAsync(1);
        await one.PostAsJsonAsync("/api/admin/connections", new { name = "kept", url = "tcp://h.example.com:23" });
        await cluster.SettleAsync();
        // Node 2's copy goes wrong behind sync's back.
        await using (var db = cluster.Db(2))
        {
            await db.Connections.Where(c => c.Name == "kept").ExecuteDeleteAsync();
            db.Settings.Add(new Setting { Key = "Sessions.IdleMinutes", Value = "99" });
            db.Applying = true;
            await db.SaveChangesAsync();
        }

        var answer = await JsonAsync(await one.PostAsync("/api/admin/nodes/2/resync", null));
        Assert.Equal("Resynced from node 1.", answer.GetProperty("message").GetString());
        await using (var db = cluster.Db(2))
        {
            Assert.True(await db.Connections.AnyAsync(c => c.Name == "kept"));
            Assert.False(await db.Settings.AnyAsync(s => s.Key == "Sessions.IdleMinutes"));
        }

        // With no peer to take it from, nothing is wiped.
        cluster.Down(1);
        var refused = await (await cluster.AdminAsync(2)).PostAsync("/api/admin/nodes/2/resync", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await using (var db = cluster.Db(2)) Assert.True(await db.Connections.AnyAsync(c => c.Name == "kept"));
    }

    [Fact]
    public async Task ANodeWithAnotherSecretIsRefused()
    {
        await using var cluster = new TestCluster(secrets: new Dictionary<int, string> { [2] = "a different secret that is also long enough" });
        cluster.Start(1);
        cluster.Start(2);
        await cluster.Sync(1).PullNowAsync();
        Assert.StartsWith("Refused this node", cluster.Sync(1).Peers[2].LastError);
    }

    [Fact]
    public async Task ThePeerApiTakesOnlySealedFreshRequestsOnce()
    {
        await using var cluster = new TestCluster();
        var node = cluster.Start(2);
        var client = node.CreateClient();
        var seal = new Seal(TestCluster.Secret);

        HttpRequestMessage Request(string op, int from, long time, byte[] body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/cluster/{op}") { Content = new ByteArrayContent(body) };
            request.Headers.Add(PeerProtocol.FromHeader, from.ToString());
            request.Headers.Add(PeerProtocol.TimeHeader, time.ToString());
            return request;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new { protocol = PeerProtocol.Version, oldest = PeerProtocol.Oldest, body = new { } },
            PeerProtocol.Json);
        var good = seal.Close(plain, Seal.RequestContext("status", 1, 2, now));

        var answer = await client.SendAsync(Request("status", 1, now, good));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var opened = seal.Open(await answer.Content.ReadAsByteArrayAsync(), Seal.ResponseContext(Seal.NonceOf(good)));
        Assert.Equal(2, JsonDocument.Parse(opened!).RootElement.GetProperty("body").GetProperty("node").GetInt32());

        // Replayed, unsealed, sealed for another operation or node, stale, or from a stranger: refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request("status", 1, now, good))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request("status", 1, now, plain))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("snapshot", 1, now, seal.Close(plain, Seal.RequestContext("status", 1, 2, now))))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("status", 1, now, seal.Close(plain, Seal.RequestContext("status", 1, 3, now))))).StatusCode);
        var stale = now - 600;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("status", 1, stale, seal.Close(plain, Seal.RequestContext("status", 1, 2, stale))))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("status", 7, now, seal.Close(plain, Seal.RequestContext("status", 7, 2, now))))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/cluster/status")).StatusCode);
    }

    [Fact]
    public async Task APeerSpeakingOnlyANewerProtocolIsToldWhy()
    {
        await using var cluster = new TestCluster();
        var client = cluster.Start(2).CreateClient();
        var seal = new Seal(TestCluster.Secret);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var plain = JsonSerializer.SerializeToUtf8Bytes(
            new { protocol = PeerProtocol.Version + 3, oldest = PeerProtocol.Version + 2, body = new { } }, PeerProtocol.Json);
        var sealedBody = seal.Close(plain, Seal.RequestContext("status", 1, 2, now));
        var request = new HttpRequestMessage(HttpMethod.Post, "/cluster/status") { Content = new ByteArrayContent(sealedBody) };
        request.Headers.Add(PeerProtocol.FromHeader, "1");
        request.Headers.Add(PeerProtocol.TimeHeader, now.ToString());
        var answer = await client.SendAsync(request);
        var opened = seal.Open(await answer.Content.ReadAsByteArrayAsync(), Seal.ResponseContext(Seal.NonceOf(sealedBody)));
        Assert.StartsWith("Protocol mismatch", JsonDocument.Parse(opened!).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithoutPeersThereIsNoPeerApi()
    {
        using var factory = RelayFactory.For("test=tcp://127.0.0.1:1");
        var response = await factory.CreateClient().PostAsync("/cluster/status", new StringContent("", Encoding.UTF8));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
