using System.Security.Cryptography;
using System.Text;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Logins;

/// <summary>Saved IBM i logins (Credentials__*).</summary>
public sealed class CredentialOptions
{
    /// <summary>
    /// The secret that seals remembered IBM i passwords, 32 characters or more, the same on
    /// every node. It is never in the database. Without it, passwords can't be remembered:
    /// they are held in memory only.
    /// </summary>
    public string Key { get; set; } = "";
}

/// <summary>
/// Seals a remembered IBM i password for the database: AES-256-GCM under a key derived from
/// Credentials:Key, bound to the row it belongs to, so a sealed password copied to another
/// user's row doesn't open. The database alone (a backup, a peer's copy) can't open any.
/// </summary>
public sealed class CredentialSeal
{
    private const string Prefix = "v1:";
    private readonly byte[]? _key;

    public CredentialSeal(CredentialOptions options)
    {
        var secret = options.Key.Trim();
        if (secret.Length == 0) return;
        if (secret.Length < 32) throw new ConfigException("Credentials__Key must have at least 32 characters.");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32,
            info: "greenglow saved logins v1"u8.ToArray());
    }

    /// <summary>Whether passwords can be remembered at all: the key is set.</summary>
    public bool Available => _key is not null;

    public string Seal(string password, string row)
    {
        if (_key is null) throw new InvalidOperationException("Credentials:Key is not set.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(password);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(row));
        CryptographicOperations.ZeroMemory(plain);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The password, or null when it can't be opened: no key, another key, or another row.</summary>
    public string? Open(string sealedPassword, string row)
    {
        if (_key is null || !sealedPassword.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var data = Convert.FromBase64String(sealedPassword[Prefix.Length..]);
            if (data.Length < 28) return null;
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(row));
            var password = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return password;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>A saved login as its user sees it: never the password.</summary>
/// <param name="Remembered">The password is kept (sealed) in the database.</param>
/// <param name="Ready">The password is to hand now: remembered, or held in this server's memory.</param>
public sealed record LoginInfo(Guid Id, string IbmUser, bool Remembered, bool Ready);

/// <summary>
/// The IBM i logins a user keeps for each system: several per system, each an IBM i user
/// profile. The password of one is held in memory while its user is signed in
/// (<see cref="CredentialVault"/>) and, only when the user ticked "remember" and the server
/// has Credentials:Key, sealed in the database as well. A password the IBM i refuses is
/// dropped from both, so it is never tried twice.
/// </summary>
public sealed class IbmLogins(IDbContextFactory<GreenGlowDb> dbs, CredentialVault vault, CredentialSeal seal,
    SettingsStore settings, TimeProvider clock)
{
    /// <summary>An administrator's switch (in the Settings table): "false" forbids remembering passwords.</summary>
    public const string AllowRememberKey = "Logins.AllowRemember";

    /// <summary>Whether a user may ask for a password to be remembered: the key is set, and no administrator forbade it.</summary>
    public bool CanRemember => seal.Available && settings.Text(AllowRememberKey) != "false";

    public async Task<IReadOnlyList<LoginInfo>> ListAsync(string userId, Target target)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var rows = await db.IbmLogins.AsNoTracking()
            .Where(l => l.UserId == userId && l.Connection!.Name == target.Name).OrderBy(l => l.IbmUser).ToListAsync();
        return rows.Select(l => Describe(l, target)).ToList();
    }

    /// <summary>
    /// Saves a login (a new one, or an existing one's new password) once its password has been
    /// checked. `remember` null keeps what the login had.
    /// </summary>
    public async Task<LoginInfo> SaveAsync(string userId, Target target, string ibmUser, string password, bool? remember)
    {
        ibmUser = ibmUser.Trim().ToUpperInvariant();
        await using var db = await dbs.CreateDbContextAsync();
        var connectionId = await db.Connections.Where(c => c.Name == target.Name).Select(c => c.Id).FirstAsync();
        var row = await db.IbmLogins.FirstOrDefaultAsync(l => l.UserId == userId && l.ConnectionId == connectionId && l.IbmUser == ibmUser);
        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new IbmLogin { UserId = userId, ConnectionId = connectionId, IbmUser = ibmUser, CreatedAt = now };
            db.IbmLogins.Add(row);
        }
        var keep = (remember ?? row.SealedPassword is not null) && CanRemember;
        row.SealedPassword = keep ? seal.Seal(password, Row(row)) : null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync();
        vault.Put(userId, target.Name, ibmUser, password);
        return Describe(row, target);
    }

    /// <summary>The password typed at IBM i sign-in is that system's login for the same user profile.</summary>
    public Task SignedInAsync(string userId, Target target, string ibmUser, string password) =>
        SaveAsync(userId, target, ibmUser, password, remember: null);

    public async Task<bool> RemoveAsync(string userId, Target target, Guid id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.IbmLogins.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId && l.Connection!.Name == target.Name);
        if (row is null) return false;
        db.IbmLogins.Remove(row);
        await db.SaveChangesAsync();
        vault.Forget(userId, target.Name, row.IbmUser);
        return true;
    }

    /// <summary>
    /// A login's user and password, to sign on with: from memory, or else opened from the
    /// database. Null when there is no such login; a null password means it must be typed.
    /// </summary>
    public async Task<(Guid Id, string IbmUser, string? Password)?> CredentialsAsync(string userId, Target target, Guid id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.IbmLogins.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId && l.Connection!.Name == target.Name);
        if (row is null) return null;
        var password = vault.Get(userId, target.Name, row.IbmUser);
        if (password is null && row.SealedPassword is { } sealedPassword && seal.Open(sealedPassword, Row(row)) is { } opened)
        {
            password = opened;
            vault.Put(userId, target.Name, row.IbmUser, opened);
        }
        return (row.Id, row.IbmUser, password);
    }

    /// <summary>The IBM i refused a login's password: it is dropped everywhere, to be typed again.</summary>
    public async Task RefusedAsync(string userId, Target target, string ibmUser)
    {
        ibmUser = ibmUser.ToUpperInvariant();
        vault.Forget(userId, target.Name, ibmUser);
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.IbmLogins.FirstOrDefaultAsync(l => l.UserId == userId && l.Connection!.Name == target.Name && l.IbmUser == ibmUser);
        if (row?.SealedPassword is null) return;
        row.SealedPassword = null;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
    }

    private LoginInfo Describe(IbmLogin row, Target target)
    {
        var remembered = row.SealedPassword is not null && seal.Available;
        return new LoginInfo(row.Id, row.IbmUser, remembered, remembered || vault.Has(row.UserId, target.Name, row.IbmUser));
    }

    /// <summary>What a sealed password is bound to.</summary>
    private static string Row(IbmLogin row) => $"{row.UserId}|{row.ConnectionId}|{row.IbmUser}";
}
