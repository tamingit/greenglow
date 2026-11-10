using System.Security.Claims;
using GreenGlow.Server.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Auth;

/// <summary>The signed-in user, from the auth cookie.</summary>
public sealed record CurrentUser(string Id, string Upn, string Name)
{
    public static CurrentUser? From(ClaimsPrincipal principal) =>
        principal.FindFirstValue(Claims.ObjectId) is { } id
            ? new CurrentUser(id, principal.FindFirstValue(Claims.Upn) ?? "", principal.FindFirstValue(Claims.Name) ?? "")
            : null;
}

/// <summary>The UPN made an administrator when it signs in (Entra:InitialAdminUpn).</summary>
public sealed record AdminSeed(string Upn);

/// <summary>Users who have signed in, or been added by an administrator, kept in the database.</summary>
public sealed class UserStore(IDbContextFactory<GreenGlowDb> dbs, TimeProvider clock, AdminSeed adminSeed)
{
    public const string AdminRole = "admin";

    /// <summary>Records a sign-in and returns the identity the cookie keeps.</summary>
    public async Task<ClaimsIdentity> SignInAsync(string objectId, string upn, string name)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        var user = await db.Users.FindAsync(objectId);
        if (user is null)
        {
            user = new AppUser { Id = objectId, Upn = upn, FirstSeenAt = now };
            db.Users.Add(user);
        }
        user.Upn = upn;
        user.DisplayName = name.Length > 0 ? name : user.DisplayName;
        user.LastSeenAt = now;
        if (IsInitialAdmin(upn)) user.IsAdmin = true;
        await db.SaveChangesAsync();

        return new ClaimsIdentity(
            [
                new Claim(Claims.ObjectId, user.Id),
                new Claim(Claims.Upn, user.Upn),
                new Claim(Claims.Name, user.DisplayName.Length > 0 ? user.DisplayName : user.Upn),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme, Claims.Name, null);
    }

    /// <summary>A new demo guest: a fresh ID, a made-up UPN that can't be anyone's, never an administrator.</summary>
    public async Task<ClaimsIdentity> SignInGuestAsync()
    {
        var id = Guid.CreateVersion7().ToString();
        var tag = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        await using (var db = await dbs.CreateDbContextAsync())
        {
            var now = clock.GetUtcNow();
            db.Users.Add(new AppUser
            {
                Id = id,
                Upn = $"guest-{tag.ToLowerInvariant()}@guest.invalid",
                DisplayName = $"Guest {tag[..4]}",
                IsGuest = true,
                FirstSeenAt = now,
                LastSeenAt = now,
            });
            await db.SaveChangesAsync();
        }
        var user = (await FindAsync(id))!;
        return new ClaimsIdentity(
            [
                new Claim(Claims.ObjectId, user.Id),
                new Claim(Claims.Upn, user.Upn),
                new Claim(Claims.Name, user.DisplayName),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme, Claims.Name, null);
    }

    /// <summary>
    /// Removes guests last seen before <paramref name="before"/> who hold none of the given
    /// sessions' users, with their colours, macros and macro keys, one row at a time so each
    /// delete is journalled for the other nodes. Connection log rows are kept.
    /// </summary>
    public async Task<int> PruneGuestsAsync(DateTimeOffset before, IReadOnlySet<string> inUse)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var old = (await db.Users.Where(u => u.IsGuest).ToListAsync())
            .Where(u => u.LastSeenAt < before && !inUse.Contains(u.Id)).ToList();
        if (old.Count == 0) return 0;
        var ids = old.Select(u => u.Id).ToList();
        db.UserColors.RemoveRange(await db.UserColors.Where(c => ids.Contains(c.UserId)).ToListAsync());
        db.MacroKeys.RemoveRange(await db.MacroKeys.Where(k => ids.Contains(k.UserId)).ToListAsync());
        var macros = await db.Macros.Include(m => m.Shares).Where(m => ids.Contains(m.OwnerUserId)).ToListAsync();
        var macroIds = macros.Select(m => m.Id).ToList();
        db.MacroKeys.RemoveRange(await db.MacroKeys.Where(k => macroIds.Contains(k.MacroId)).ToListAsync());
        db.Macros.RemoveRange(macros);
        db.Users.RemoveRange(old);
        await db.SaveChangesAsync();
        return old.Count;
    }

    public async Task<bool> IsKnownAsync(string objectId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Users.AnyAsync(u => u.Id == objectId);
    }

    private bool IsInitialAdmin(string upn) =>
        adminSeed.Upn.Length > 0 && string.Equals(adminSeed.Upn.Trim(), upn.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>At start: the initial administrator, if already known, is an administrator.</summary>
    public async Task PromoteInitialAdminAsync()
    {
        if (adminSeed.Upn.Trim().Length == 0) return;
        await using var db = await dbs.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Upn == adminSeed.Upn.Trim());
        if (user is null || user.IsAdmin) return;
        user.IsAdmin = true;
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AppUser>> AdminsAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => u.IsAdmin).OrderBy(u => u.Upn).ToListAsync();
    }

    /// <summary>
    /// Makes someone an administrator. Someone who hasn't signed in yet is added from the
    /// directory, so they are one from their first sign-in.
    /// </summary>
    public async Task<AppUser> MakeAdminAsync(string objectId, string upn, string name)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var user = await db.Users.FindAsync(objectId);
        if (user is null)
        {
            user = new AppUser { Id = objectId, Upn = upn, DisplayName = name, FirstSeenAt = clock.GetUtcNow() };
            db.Users.Add(user);
        }
        user.IsAdmin = true;
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Removes an administrator; refused for the last one, so the server keeps one.</summary>
    public async Task<bool> RemoveAdminAsync(string objectId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = await db.Users.FindAsync(objectId);
        if (user is null || !user.IsAdmin) return true;
        if (await db.Users.CountAsync(u => u.IsAdmin) <= 1) return false;
        user.IsAdmin = false;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<AppUser?> FindAsync(string objectId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == objectId);
    }
}
