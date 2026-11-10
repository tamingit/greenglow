using System.Security.Cryptography;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Sessions;

/// <summary>A share link as a recipient may use it.</summary>
public sealed record ShareGrant(Guid ShareId, Guid SessionId, FrontendRole Role);

/// <summary>
/// Sharing a live session: its owner names people (by UPN, from the directory) with view-only
/// or full access and gets a link. Only those people can use the link, and only while the
/// session is open and the share isn't revoked.
/// </summary>
public sealed class Shares(IDbContextFactory<GreenGlowDb> dbs, IDirectory directory, TimeProvider clock)
{
    public const int MaxRecipients = 20;

    /// <summary>
    /// How long after a share is made a recipient stored without a directory ID (only a UPN) may
    /// still open it by UPN. A recipient with a directory ID is matched on that, with no time
    /// limit; the weaker UPN match is allowed only briefly, because a UPN can be reassigned to a
    /// different person later (decided 13 October 2026).
    /// </summary>
    public static readonly TimeSpan UpnFallbackWindow = TimeSpan.FromMinutes(30);

    public sealed record Recipient(string Upn, ShareAccess Access);

    public sealed class ShareException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>Makes a share link for the session's owner. Every UPN must be in the directory.</summary>
    public async Task<SessionShare> CreateAsync(HostSession session, CurrentUser owner, IReadOnlyList<Recipient> recipients)
    {
        if (recipients.Count == 0) throw new ShareException("recipients", "Name at least one person to share with.");
        if (recipients.Count > MaxRecipients) throw new ShareException("recipients", $"Share with at most {MaxRecipients} people at once.");
        var share = new SessionShare
        {
            SessionId = session.Id,
            OwnerUserId = owner.Id,
            Token = Base64Url(RandomNumberGenerator.GetBytes(24)),
            CreatedAt = clock.GetUtcNow(),
        };
        foreach (var recipient in recipients)
        {
            var upn = recipient.Upn.Trim();
            if (upn.Length is 0 or > 256) throw new ShareException("upn", "Each person needs a UPN.");
            var found = await directory.FindUserAsync(upn)
                        ?? throw new ShareException("not-found", $"No one in the directory has the UPN {upn}.");
            if (found.Id == owner.Id) throw new ShareException("self", "You already have this session.");
            if (share.Recipients.Any(r => r.UserId == found.Id)) continue;
            share.Recipients.Add(new ShareRecipient
            {
                UserId = found.Id,
                Upn = found.Upn ?? upn,
                DisplayName = found.Name,
                Access = recipient.Access,
            });
        }
        await using var db = await dbs.CreateDbContextAsync();
        db.Shares.Add(share);
        await db.SaveChangesAsync();
        return share;
    }

    public async Task<IReadOnlyList<SessionShare>> ForSessionAsync(Guid sessionId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Shares.AsNoTracking().Include(s => s.Recipients)
            .Where(s => s.SessionId == sessionId && s.RevokedAt == null).OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync();
    }

    /// <summary>Revokes a share of the owner's session. Returns false if there is none.</summary>
    public async Task<bool> RevokeAsync(Guid sessionId, Guid shareId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var share = await db.Shares.FirstOrDefaultAsync(s => s.Id == shareId && s.SessionId == sessionId && s.RevokedAt == null);
        if (share is null) return false;
        share.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Changes one person's access on a live share of the session. Returns them, or null if the
    /// share or the person isn't there.
    /// </summary>
    public async Task<ShareRecipient?> ChangeAccessAsync(Guid sessionId, Guid shareId, Guid recipientId, ShareAccess access)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var recipient = await db.ShareRecipients.FirstOrDefaultAsync(r =>
            r.Id == recipientId && r.ShareId == shareId && r.Share!.SessionId == sessionId && r.Share.RevokedAt == null);
        if (recipient is null) return null;
        recipient.Access = access;
        await db.SaveChangesAsync();
        return recipient;
    }

    /// <summary>
    /// Takes one person off a live share of the session; a share left with no one is revoked.
    /// Returns them, or null if the share or the person isn't there.
    /// </summary>
    public async Task<(ShareRecipient Recipient, bool Revoked)?> RemoveAsync(Guid sessionId, Guid shareId, Guid recipientId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var share = await db.Shares.Include(s => s.Recipients)
            .FirstOrDefaultAsync(s => s.Id == shareId && s.SessionId == sessionId && s.RevokedAt == null);
        var recipient = share?.Recipients.FirstOrDefault(r => r.Id == recipientId);
        if (share is null || recipient is null) return null;
        share.Recipients.Remove(recipient);
        db.ShareRecipients.Remove(recipient);
        var revoked = share.Recipients.Count == 0;
        if (revoked) share.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return (recipient, revoked);
    }

    /// <summary>
    /// Takes this user off every live share of the session that names them; a share left with
    /// no one is revoked. Returns the shares they left.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LeaveAsync(Guid sessionId, CurrentUser user)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var named = await db.Shares.Include(s => s.Recipients)
            .Where(s => s.SessionId == sessionId && s.RevokedAt == null
                        && s.Recipients.Any(r => r.UserId == user.Id || r.Upn == user.Upn))
            .ToListAsync();
        foreach (var share in named)
        {
            foreach (var recipient in share.Recipients.Where(r => r.UserId == user.Id
                         || string.Equals(r.Upn, user.Upn, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                share.Recipients.Remove(recipient);
                db.ShareRecipients.Remove(recipient);
            }
            if (share.Recipients.Count == 0) share.RevokedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync();
        return named.Select(s => s.Id).ToList();
    }

    /// <summary>The people named on the live shares of these sessions, by user ID (when known) and UPN.</summary>
    public async Task<IReadOnlyList<(string? UserId, string Upn)>> PeopleOfAsync(IReadOnlyCollection<Guid> sessionIds)
    {
        if (sessionIds.Count == 0) return [];
        await using var db = await dbs.CreateDbContextAsync();
        var rows = await db.ShareRecipients.AsNoTracking()
            .Where(r => sessionIds.Contains(r.Share!.SessionId) && r.Share.RevokedAt == null)
            .Select(r => new { r.UserId, r.Upn })
            .ToListAsync();
        return rows.Select(r => (r.UserId, r.Upn)).ToList();
    }

    /// <summary>What a link gives this user: nothing unless they are one of its recipients.</summary>
    public async Task<ShareGrant?> ResolveAsync(string token, CurrentUser user)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64) return null;
        await using var db = await dbs.CreateDbContextAsync();
        var share = await db.Shares.AsNoTracking().Include(s => s.Recipients)
            .FirstOrDefaultAsync(s => s.Token == token && s.RevokedAt == null);
        var recipient = share is null ? null : MatchRecipient(share, user);
        return share is null || recipient is null
            ? null
            : new ShareGrant(share.Id, share.SessionId, recipient.Access == ShareAccess.FullAccess ? FrontendRole.Full : FrontendRole.View);
    }

    /// <summary>
    /// The recipient entry naming this user: by the immutable directory ID, or — only when a
    /// recipient was stored without one — by UPN, and then only within <see cref="UpnFallbackWindow"/>
    /// of the share being made (see the field's note on UPN reassignment).
    /// </summary>
    private ShareRecipient? MatchRecipient(SessionShare share, CurrentUser user)
    {
        var byId = share.Recipients.FirstOrDefault(r => r.UserId == user.Id);
        if (byId is not null) return byId;
        if (clock.GetUtcNow() - share.CreatedAt >= UpnFallbackWindow) return null;
        return share.Recipients.FirstOrDefault(r => r.UserId == null
            && string.Equals(r.Upn, user.Upn, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The live shares naming this user, by session, with the best access any of them gives.</summary>
    public async Task<IReadOnlyDictionary<Guid, (FrontendRole Role, string Token)>> SharedWithAsync(CurrentUser user)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var rows = await db.ShareRecipients.AsNoTracking()
            .Where(r => (r.UserId == user.Id || (r.UserId == null && r.Upn == user.Upn)) && r.Share!.RevokedAt == null)
            .Select(r => new { r.Share!.SessionId, r.Access, r.Share.Token, r.ShareId, r.UserId, r.Share.CreatedAt })
            .ToListAsync();
        // A recipient stored with only a UPN counts only within the fallback window (done in
        // memory: SQLite can't compare the stored DateTimeOffset values).
        var now = clock.GetUtcNow();
        return rows
            .Where(r => r.UserId == user.Id || now - r.CreatedAt < UpnFallbackWindow)
            .GroupBy(r => r.SessionId).ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g.OrderByDescending(r => r.Access == ShareAccess.FullAccess).ThenByDescending(r => r.CreatedAt).First();
                    return (best.Access == ShareAccess.FullAccess ? FrontendRole.Full : FrontendRole.View, best.Token);
                });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
