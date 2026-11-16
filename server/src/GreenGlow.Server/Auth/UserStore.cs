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

/// <summary>A sign-in under a name that an account left from another sign-in method still has.</summary>
public sealed class NameTakenException(string name) : Exception($"The name {name} belongs to an account from another sign-in method.");

/// <summary>The UPN made an administrator when it signs in (Entra:InitialAdminUpn).</summary>
public sealed record AdminSeed(string Upn);

/// <summary>Users who have signed in, or been added by an administrator, kept in the database.</summary>
public sealed class UserStore(IDbContextFactory<GreenGlowDb> dbs, TimeProvider clock, AdminSeed adminSeed)
{
    public const string AdminRole = "admin";

    /// <summary>Records a sign-in and returns the identity the cookie keeps. Null: the account is switched off.</summary>
    public async Task<ClaimsIdentity?> SignInAsync(string objectId, string upn, string name, UserSource source)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        var user = await db.Users.FindAsync(objectId);
        if (user is null)
        {
            user = new AppUser { Id = objectId, Upn = upn, FirstSeenAt = now, Source = source };
            db.Users.Add(user);
        }
        if (user.Disabled) return null;
        user.Upn = upn;
        user.DisplayName = name.Length > 0 ? name : user.DisplayName;
        user.LastSeenAt = now;
        if (source == UserSource.Entra && IsInitialAdmin(upn)) user.IsAdmin = true;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The name belongs to an account left from another sign-in method.
            throw new NameTakenException(upn);
        }
        return Identity(user);
    }

    /// <summary>Entra ID (and the dev login, which stands in for it): never refused here.</summary>
    public async Task<ClaimsIdentity> SignInAsync(string objectId, string upn, string name) =>
        await SignInAsync(objectId, upn, name, UserSource.Entra)
        ?? throw new InvalidOperationException("That account is switched off.");

    /// <summary>A sign-in by an account that is already in the table (a local account, the breakglass administrator).</summary>
    public async Task<ClaimsIdentity> SignInKnownAsync(string id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id) ?? throw new InvalidOperationException("No such user.");
        user.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return Identity(user);
    }

    public static ClaimsIdentity Identity(AppUser user) => new(
        [
            new Claim(Claims.ObjectId, user.Id),
            new Claim(Claims.Upn, user.Upn),
            new Claim(Claims.Name, user.DisplayName.Length > 0 ? user.DisplayName : user.Upn),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme, Claims.Name, null);

    /// <summary>The ID of an IBM i user profile's account: always the same for the same profile.</summary>
    public static string IbmUserId(string profile) => AuthSetup.DevObjectId("ibmi:" + profile.Trim().ToUpperInvariant());

    public async Task<AppUser?> FindByUpnAsync(string upn)
    {
        upn = upn.Trim();
        if (upn.Length is 0 or > 256) return null;
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Upn == upn);
    }

    /// <summary>Changes one user's row; false when there is no such user.</summary>
    public async Task<bool> UpdateAsync(string id, Action<AppUser> change)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Users.FindAsync(id) is not { } user) return false;
        change(user);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<AppUser>> AllAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => !u.IsGuest).OrderBy(u => u.Upn).ToListAsync();
    }

    /// <summary>Removes users with everything of theirs (see <see cref="RemoveAsync(GreenGlowDb, List{AppUser})"/>).</summary>
    public async Task<bool> RemoveAsync(string id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Users.FindAsync(id) is not { } user) return false;
        await RemoveAsync(db, [user]);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Marks users for removal with their colours, saved IBM i logins, macros and macro keys,
    /// one row at a time so each delete is journalled for the other nodes. Connection log rows
    /// are kept. The caller saves.
    /// </summary>
    private static async Task RemoveAsync(GreenGlowDb db, List<AppUser> users)
    {
        var ids = users.Select(u => u.Id).ToList();
        db.UserColors.RemoveRange(await db.UserColors.Where(c => ids.Contains(c.UserId)).ToListAsync());
        db.IbmLogins.RemoveRange(await db.IbmLogins.Where(l => ids.Contains(l.UserId)).ToListAsync());
        db.MacroKeys.RemoveRange(await db.MacroKeys.Where(k => ids.Contains(k.UserId)).ToListAsync());
        var macros = await db.Macros.Include(m => m.Shares).Where(m => ids.Contains(m.OwnerUserId)).ToListAsync();
        var macroIds = macros.Select(m => m.Id).ToList();
        db.MacroKeys.RemoveRange(await db.MacroKeys.Where(k => macroIds.Contains(k.MacroId)).ToListAsync());
        db.Macros.RemoveRange(macros);
        db.Users.RemoveRange(users);
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
        await RemoveAsync(db, old);
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
        // The breakglass administrator is always one; it isn't removed, only given a new password.
        if (user.Source == UserSource.Breakglass) return false;
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
