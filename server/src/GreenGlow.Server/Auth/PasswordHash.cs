using System.Globalization;
using System.Security.Cryptography;

namespace GreenGlow.Server.Auth;

/// <summary>
/// Passwords of local accounts and of the breakglass administrator, hashed with PBKDF2
/// (HMAC-SHA-256, a random salt each) and kept as "pbkdf2-sha256$iterations$salt$hash". The
/// iterations travel with the hash, so they can be raised without breaking old ones.
/// </summary>
public static class PasswordHash
{
    public const int Iterations = 600_000;
    public const int MinLength = 12;
    public const int MaxLength = 128;
    private const string Scheme = "pbkdf2-sha256";

    /// <summary>A hash that matches no password, to spend the same time when there is no account.</summary>
    private static readonly string Nothing = Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether the password is the hashed one. Takes as long for a missing or unreadable hash.</summary>
    public static bool Verify(string? stored, string password)
    {
        var parts = (stored ?? Nothing).Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 1 or > 10_000_000)
        {
            Verify(Nothing, password);
            return false;
        }
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            Verify(Nothing, password);
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && stored is not null;
    }

    /// <summary>Why a new password can't be used, or null when it can.</summary>
    public static string? Problem(string? password) => password switch
    {
        null or "" => "Type a password.",
        { Length: < MinLength } => $"The password must have at least {MinLength} characters.",
        { Length: > MaxLength } => $"The password can have at most {MaxLength} characters.",
        _ => null,
    };
}
