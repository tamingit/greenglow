using System.Net;
using GreenGlow.Server.People;
using Microsoft.Extensions.DependencyInjection;

namespace GreenGlow.Server.Tests;

/// <summary>When Entra ID can't be searched, the page gets the reason, not a server error.</summary>
public sealed class DirectoryErrorTests
{
    [Theory]
    [InlineData("/api/directory/users?q=pat")]
    [InlineData("/api/admin/directory/groups?q=ops")]
    public async Task AFailedSearchAnswers502WithTheReason(string path)
    {
        using var factory = new RelayFactory(new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = T.Origin,
            ["Entra:InitialAdminUpn"] = RelayFactory.DefaultUpn,
        })
        {
            ExtraServices = s => s.AddSingleton<IDirectory, RefusingDirectory>(),
        };
        var client = await factory.SignedInClientAsync();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"directory\"", body);
        Assert.Contains("needs the application permissions", body);
    }

    private sealed class RefusingDirectory : IDirectory
    {
        private static Exception Refused() =>
            new HttpRequestException("Microsoft Graph refused the request (403): the app registration needs the application permissions X");

        public Task<DirectoryEntry?> FindUserAsync(string upn, CancellationToken cancel = default) =>
            Task.FromResult<DirectoryEntry?>(new DirectoryEntry(GreenGlow.Server.Auth.AuthSetup.DevObjectId(upn), upn, upn));

        public Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancel = default) => throw Refused();
        public Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancel = default) => throw Refused();
        public Task<IReadOnlyList<DirectoryEntry>> FindGroupsByNameAsync(string name, CancellationToken cancel = default) => throw Refused();
        public Task<IReadOnlySet<string>> MemberGroupsAsync(string userId, CancellationToken cancel = default) => throw Refused();
    }
}
