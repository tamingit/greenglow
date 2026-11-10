using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

/// <summary>
/// How a share link resolves to a recipient. A recipient stored with the directory object ID is
/// matched on it with no time limit; one stored with only a UPN (which can later be reassigned to
/// a different person) is matched on the UPN only within Shares.UpnFallbackWindow.
/// </summary>
public sealed class ShareResolveTests
{
    private static readonly CurrentUser Pat = new("pat-object-id", "pat@example.com", "Pat");

    private static async Task<string> AddShareAsync(RelayFactory factory, DateTimeOffset createdAt, string? userId, string upn)
    {
        var token = "tok" + Guid.NewGuid().ToString("N");
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync();
        db.Shares.Add(new SessionShare
        {
            SessionId = Guid.NewGuid(),
            OwnerUserId = "owner-object-id",
            Token = token,
            CreatedAt = createdAt,
            Recipients = { new ShareRecipient { UserId = userId, Upn = upn, Access = ShareAccess.FullAccess } },
        });
        await db.SaveChangesAsync();
        return token;
    }

    [Fact]
    public async Task AUpnOnlyRecipientResolvesOnlyWithinTheWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new RelayFactory(new Dictionary<string, string?> { ["Server:PublicUrl"] = T.Origin })
        {
            ExtraServices = s => s.AddSingleton<TimeProvider>(clock),
        };
        var shares = factory.Services.GetRequiredService<Shares>();
        var token = await AddShareAsync(factory, clock.GetUtcNow(), userId: null, upn: Pat.Upn);

        Assert.NotNull(await shares.ResolveAsync(token, Pat));

        clock.Advance(Shares.UpnFallbackWindow + TimeSpan.FromMinutes(1));
        Assert.Null(await shares.ResolveAsync(token, Pat));
    }

    [Fact]
    public async Task ARecipientWithAnObjectIdResolvesWithNoTimeLimit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new RelayFactory(new Dictionary<string, string?> { ["Server:PublicUrl"] = T.Origin })
        {
            ExtraServices = s => s.AddSingleton<TimeProvider>(clock),
        };
        var shares = factory.Services.GetRequiredService<Shares>();
        var token = await AddShareAsync(factory, clock.GetUtcNow(), userId: Pat.Id, upn: Pat.Upn);

        clock.Advance(Shares.UpnFallbackWindow + TimeSpan.FromDays(1));
        Assert.NotNull(await shares.ResolveAsync(token, Pat));
    }

    [Fact]
    public async Task AUpnMatchNeverStandsInForADifferentObjectId()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new RelayFactory(new Dictionary<string, string?> { ["Server:PublicUrl"] = T.Origin })
        {
            ExtraServices = s => s.AddSingleton<TimeProvider>(clock),
        };
        var shares = factory.Services.GetRequiredService<Shares>();
        // Same UPN, but a different object ID: the person it was shared with has left and the UPN
        // was reassigned. Even within the window, the UPN must not match a row that has an ID.
        var token = await AddShareAsync(factory, clock.GetUtcNow(), userId: "someone-elses-object-id", upn: Pat.Upn);

        Assert.Null(await shares.ResolveAsync(token, Pat));
    }
}
