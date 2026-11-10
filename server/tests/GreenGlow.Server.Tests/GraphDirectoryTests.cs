using System.Net;
using System.Text;
using System.Text.Json;
using GreenGlow.Server.Auth;
using GreenGlow.Server.People;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

public sealed class GraphDirectoryTests
{
    private sealed record Seen(HttpMethod Method, Uri Uri, string? Bearer, string Body, Dictionary<string, string> Headers);

    private sealed class FakeGraph(Func<Seen, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public int TokenRequests => Requests.Count(r => r.Uri.Host == "login.microsoftonline.com");

        /// <summary>The access token to hand out; "token-N" when unset.</summary>
        public string? AccessToken { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var seen = new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
            Requests.Add(seen);
            if (seen.Uri.Host == "login.microsoftonline.com")
                return Json($$"""{"access_token":"{{AccessToken ?? $"token-{TokenRequests}"}}","expires_in":3600}""");
            return answer(seen);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GraphDirectory Directory, FakeGraph Graph, FakeTimeProvider Clock) Make(Func<Seen, HttpResponseMessage> answer)
    {
        var graph = new FakeGraph(answer);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        var entra = new EntraOptions { TenantId = "tenant-1", ClientId = "client-1", ClientSecret = "s3cret" };
        return (new GraphDirectory(new HttpClient(graph), entra, clock), graph, clock);
    }

    private static string SearchTerm(Uri uri) =>
        Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("$search=", StringComparison.Ordinal))["$search=".Length..]);

    [Fact]
    public async Task ItFetchesATokenWithClientCredentials()
    {
        var (directory, graph, _) = Make(_ => Json("""{"id":"u1","displayName":"Pat","userPrincipalName":"pat@example.com"}"""));
        await directory.FindUserAsync("pat@example.com");

        var token = graph.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token", token.Uri.ToString());
        var form = token.Body.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("client-1", form["client_id"]);
        Assert.Equal("s3cret", form["client_secret"]);
        Assert.Equal("https://graph.microsoft.com/.default", form["scope"]);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("Bearer token-1", graph.Requests[1].Bearer);
    }

    [Fact]
    public async Task ATokenIsReusedUntilItNearsExpiry()
    {
        var (directory, graph, clock) = Make(_ => Json("""{"value":[]}"""));
        await directory.SearchUsersAsync("pat");
        clock.Advance(TimeSpan.FromMinutes(50));
        await directory.SearchUsersAsync("pat");
        Assert.Equal(1, graph.TokenRequests);

        // 3600 seconds, less a two minute margin: 58 minutes in, it is fetched again.
        clock.Advance(TimeSpan.FromMinutes(9));
        await directory.SearchUsersAsync("pat");
        Assert.Equal(2, graph.TokenRequests);
        Assert.Equal("Bearer token-2", graph.Requests.Last().Bearer);
    }

    [Fact]
    public async Task FindUserMapsTheUserAndEscapesTheUpn()
    {
        var (directory, graph, _) = Make(_ => Json("""{"id":"u1","displayName":"Pat Doe","userPrincipalName":"pat+x@example.com"}"""));
        var user = await directory.FindUserAsync("pat+x@example.com");

        Assert.Equal(new DirectoryEntry("u1", "Pat Doe", "pat+x@example.com"), user);
        var request = graph.Requests.Single(r => r.Uri.Host == "graph.microsoft.com");
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1.0/users/pat%2Bx%40example.com", request.Uri.AbsolutePath);
        Assert.Contains("$select=id,displayName,userPrincipalName", request.Uri.Query);
        Assert.StartsWith("Bearer ", request.Bearer);
    }

    [Fact]
    public async Task FindUserIsNullWhenGraphHasNoSuchUser()
    {
        var (directory, _, _) = Make(_ => Json("""{"error":{}}""", HttpStatusCode.NotFound));
        Assert.Null(await directory.FindUserAsync("nobody@example.com"));
    }

    [Fact]
    public async Task SearchUsersAsksForEventualConsistencyAndMapsUsers()
    {
        var (directory, graph, _) = Make(_ => Json("""
            {"value":[{"id":"u1","displayName":"Pat Doe","userPrincipalName":"pat@example.com"},
                      {"id":"u2","userPrincipalName":"sam@example.com"}]}
            """));
        var users = await directory.SearchUsersAsync("pat");

        Assert.Equal([new DirectoryEntry("u1", "Pat Doe", "pat@example.com"), new DirectoryEntry("u2", "", "sam@example.com")], users);
        var request = graph.Requests.Last();
        Assert.Equal("eventual", request.Headers["ConsistencyLevel"]);
        Assert.Equal("/v1.0/users", request.Uri.AbsolutePath);
        Assert.Equal("\"displayName:pat\" OR \"userPrincipalName:pat\"", SearchTerm(request.Uri));
    }

    [Fact]
    public async Task AQuoteInASearchCannotBreakOutOfTheQuotedTerm()
    {
        var (directory, graph, _) = Make(_ => Json("""{"value":[]}"""));
        await directory.SearchUsersAsync("a\" OR \"b\\");

        Assert.Equal("\"displayName:a\\\" OR \\\"b\\\\\" OR \"userPrincipalName:a\\\" OR \\\"b\\\\\"",
            SearchTerm(graph.Requests.Last().Uri));
    }

    [Fact]
    public async Task SearchGroupsMapsGroups()
    {
        var (directory, graph, _) = Make(_ => Json("""{"value":[{"id":"g1","displayName":"Developers"}]}"""));
        var groups = await directory.SearchGroupsAsync("dev");

        Assert.Equal([new DirectoryEntry("g1", "Developers", null)], groups);
        var request = graph.Requests.Last();
        Assert.Equal("/v1.0/groups", request.Uri.AbsolutePath);
        Assert.Equal("eventual", request.Headers["ConsistencyLevel"]);
        Assert.Equal("\"displayName:dev\"", SearchTerm(request.Uri));
    }

    [Fact]
    public async Task MemberGroupsPostsAndReturnsTheIds()
    {
        var (directory, graph, _) = Make(_ => Json("""{"value":["g1","g2"]}"""));
        var groups = await directory.MemberGroupsAsync("user-1");

        Assert.Equal(new HashSet<string> { "g1", "g2" }, groups);
        var request = graph.Requests.Last();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1.0/users/user-1/getMemberGroups", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body);
        Assert.False(body.RootElement.GetProperty("securityEnabledOnly").GetBoolean());
    }

    [Fact]
    public async Task AGraphFailureDoesNotCarryTheResponseBody()
    {
        var (directory, _, _) = Make(_ => Json("""{"error":"echoed query: secret-term"}""", HttpStatusCode.InternalServerError));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => directory.SearchUsersAsync("secret-term"));
        Assert.DoesNotContain("secret-term", error.Message);
        Assert.Contains("500", error.Message);
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public async Task GroupsAreFoundByTheirExactNameWithQuotesEscaped()
    {
        var (directory, graph, _) = Make(_ => Json("""{"value":[{"id":"g1","displayName":"Pat's group"}]}"""));
        var groups = await directory.FindGroupsByNameAsync("Pat's group");
        Assert.Equal([new DirectoryEntry("g1", "Pat's group", null)], groups);
        var query = graph.Requests[1].Uri;
        Assert.Equal("/v1.0/groups", query.AbsolutePath);
        var filter = Uri.UnescapeDataString(query.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("$filter=", StringComparison.Ordinal))["$filter=".Length..]);
        Assert.Equal("displayName eq 'Pat''s group'", filter);
    }

    /// <summary>A token shaped like Entra's, carrying these application roles.</summary>
    private static string TokenWithRoles(params string[] roles)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"none"}""")}.{Part(System.Text.Json.JsonSerializer.Serialize(new { roles }))}.sig";
    }

    [Theory]
    [InlineData(new string[0], new[] { "User.ReadBasic.All", "GroupMember.Read.All" })]
    [InlineData(new[] { "User.ReadBasic.All" }, new[] { "GroupMember.Read.All" })]
    [InlineData(new[] { "User.ReadBasic.All", "GroupMember.Read.All" }, new string[0])]
    [InlineData(new[] { "Directory.Read.All" }, new string[0])]
    [InlineData(new[] { "User.Read.All", "Group.Read.All" }, new string[0])]
    public async Task TheStartupCheckNamesTheMissingPermissions(string[] granted, string[] missing)
    {
        var (directory, graph, _) = Make(_ => Json("{}"));
        graph.AccessToken = TokenWithRoles(granted);
        Assert.Equal(missing, await directory.MissingRolesAsync());
    }

    [Fact]
    public async Task AForbiddenSearchSaysWhichPermissionsAreNeeded()
    {
        var (directory, _, _) = Make(_ => Json("""{"error":{"code":"Authorization_RequestDenied"}}""", HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => directory.SearchUsersAsync("pat"));
        Assert.Contains("User.ReadBasic.All and GroupMember.Read.All", error.Message);
        Assert.Contains("admin consent", error.Message);
    }
}
