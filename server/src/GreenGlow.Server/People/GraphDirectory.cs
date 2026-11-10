using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GreenGlow.Server.Auth;

namespace GreenGlow.Server.People;

/// <summary>
/// Microsoft Graph with the app's own identity (client credentials). It needs the application
/// permissions User.ReadBasic.All and GroupMember.Read.All, with admin consent.
/// </summary>
public sealed class GraphDirectory(HttpClient http, EntraOptions entra, TimeProvider clock) : IDirectory
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public async Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(upn)}?$select=id,displayName,userPrincipalName", null, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await Ensure(response);
        return User(await response.Content.ReadFromJsonAsync<JsonElement>(cancel));
    }

    public Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default) =>
        SearchAsync("users", $"\"displayName:{Escape(query)}\" OR \"userPrincipalName:{Escape(query)}\"",
            "id,displayName,userPrincipalName", User, cancel);

    public Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default) =>
        SearchAsync("groups", $"\"displayName:{Escape(query)}\"", "id,displayName",
            g => new DirectoryEntry(g.GetProperty("id").GetString()!, g.GetProperty("displayName").GetString() ?? "", null), cancel);

    public async Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default)
    {
        // OData string literals double their single quotes.
        var filter = $"displayName eq '{name.Trim().Replace("'", "''")}'";
        using var response = await SendAsync(HttpMethod.Get,
            $"/groups?$filter={Uri.EscapeDataString(filter)}&$select=id,displayName&$top=10", null, cancel);
        await Ensure(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancel);
        return body.GetProperty("value").EnumerateArray()
            .Select(g => new DirectoryEntry(g.GetProperty("id").GetString()!, g.GetProperty("displayName").GetString() ?? "", null))
            .ToList();
    }

    public async Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/users/{Uri.EscapeDataString(userId)}/getMemberGroups",
            JsonContent.Create(new { securityEnabledOnly = false }), cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new HashSet<string>();
        await Ensure(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancel);
        return body.GetProperty("value").EnumerateArray().Select(v => v.GetString()!).ToHashSet();
    }

    private async Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string collection, string search, string select,
        Func<JsonElement, DirectoryEntry> map, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(search)) return [];
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{Graph}/{collection}?$search={Uri.EscapeDataString(search)}&$select={select}&$top=15&$orderby=displayName");
        request.Headers.Add("ConsistencyLevel", "eventual");
        using var response = await SendAsync(request, cancel);
        await Ensure(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancel);
        return body.GetProperty("value").EnumerateArray().Select(map).ToList();
    }

    private static DirectoryEntry User(JsonElement u) => new(
        u.GetProperty("id").GetString()!,
        u.TryGetProperty("displayName", out var name) ? name.GetString() ?? "" : "",
        u.GetProperty("userPrincipalName").GetString());

    /// <summary>Graph $search terms are quoted; quotes and backslashes in them must be escaped.</summary>
    private static string Escape(string query) => query.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancel)
    {
        var request = new HttpRequestMessage(method, Graph + path) { Content = content };
        return SendAsync(request, cancel);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancel));
        return await http.SendAsync(request, cancel);
    }

    /// <summary>The application permissions the server needs, with admin consent.</summary>
    public static readonly string[] RequiredRoles = ["User.ReadBasic.All", "GroupMember.Read.All"];

    private static Task Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        // The status only: Graph error bodies can echo the query.
        var message = response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? $"Microsoft Graph refused the request (403): the app registration needs the application permissions {string.Join(" and ", RequiredRoles)}, with admin consent"
            : $"Microsoft Graph returned {(int)response.StatusCode}";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    /// <summary>
    /// The required application permissions the app's token doesn't carry (from its roles
    /// claim), so a missing permission or consent shows at startup, not at the first search.
    /// </summary>
    public async Task<IReadOnlyList<string>> MissingRolesAsync(CancellationToken cancel = default)
    {
        var token = await TokenAsync(cancel);
        var payload = token.Split('.') is [_, var middle, _] ? middle : "";
        var padded = payload.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var claims = JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement;
        var granted = claims.TryGetProperty("roles", out var roles)
            ? roles.EnumerateArray().Select(r => r.GetString()).ToHashSet()
            : [];
        // A broader permission covers a narrower one.
        if (granted.Contains("User.Read.All") || granted.Contains("Directory.Read.All")) granted.Add("User.ReadBasic.All");
        if (granted.Contains("Group.Read.All") || granted.Contains("Directory.Read.All")) granted.Add("GroupMember.Read.All");
        return RequiredRoles.Where(r => !granted.Contains(r)).ToList();
    }

    private async Task<string> TokenAsync(CancellationToken cancel)
    {
        await _tokenLock.WaitAsync(cancel);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpires) return _token;
            using var response = await http.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(entra.TenantId)}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = entra.ClientId,
                    ["client_secret"] = entra.ClientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials",
                }), cancel);
            await Ensure(response);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancel);
            _token = body.GetProperty("access_token").GetString();
            _tokenExpires = clock.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32() - 120);
            return _token!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
