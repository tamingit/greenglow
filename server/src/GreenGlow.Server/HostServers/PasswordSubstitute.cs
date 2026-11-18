using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GreenGlow.Engine;

namespace GreenGlow.Server.HostServers;

/// <summary>
/// The password substitute a host server sign-on sends in place of the password, built from
/// both random seeds so it is good for one sign-on only. QPWDLVL 0 and 1 use the DES scheme
/// (passwords of up to 10 characters, case-insensitive), 2 and 3 the SHA-1 scheme (up to 128,
/// case-sensitive), and 4 (IBM i 7.5 and later) the SHA-512 scheme, whose token is PBKDF2 with
/// HMAC-SHA-512. The steps follow IBM's Toolbox for Java (AS400ImplRemote: generateToken,
/// generatePasswordSubstitute, generateShaToken, generateShaSubstitute,
/// generatePwdTokenForPasswordLevel4, generateSha512Substitute). Every buffer holding the
/// password is cleared before returning.
/// </summary>
public static class PasswordSubstitute
{
    public const byte DesScheme = 0x01;
    public const byte ShaScheme = 0x03;
    public const byte Sha512Scheme = 0x07;

    /// <summary>PBKDF2 rounds for the QPWDLVL 4 password token: IBM's number, not ours to choose.</summary>
    private const int Level4Iterations = 10022;
    private static readonly byte[] Sequence = [0, 0, 0, 0, 0, 0, 0, 1];

    /// <summary>The scheme byte for Start Server and the substitute, or a SignOnException that sent nothing.</summary>
    public static (byte Scheme, byte[] Substitute) For(int passwordLevel, string user, string password,
        ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed) => passwordLevel switch
    {
        0 or 1 => (DesScheme, Des(UserIdEbcdic(user), password, clientSeed, serverSeed)),
        2 or 3 => (ShaScheme, Sha(user, password, clientSeed, serverSeed)),
        4 => (Sha512Scheme, Sha512(user, password, clientSeed, serverSeed)),
        _ => throw new SignOnException(SignOnFailure.Unsupported,
            $"This IBM i uses password level {passwordLevel}, which GreenGlow can't sign on with."),
    };

    /// <summary>The user ID in CCSID 37, upper case, padded with blanks to 10 bytes.</summary>
    public static byte[] UserIdEbcdic(string user) => Ebcdic.Encode(user.ToUpperInvariant().PadRight(10));

    public static byte[] Des(byte[] userId, string password, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        var pwd = DesPassword(password);
        try
        {
            var token = DesToken(userId, pwd);
            try
            {
                return DesSubstitute(userId, token, clientSeed, serverSeed, Sequence);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwd);
        }
    }

    /// <summary>A password as QPWDLVL 0 and 1 hold it: upper case, CCSID 37, padded with blanks to 10 bytes.</summary>
    private static byte[] DesPassword(string password, bool isNew = false)
    {
        var failure = isNew ? SignOnFailure.NewPasswordRefused : SignOnFailure.InvalidPassword;
        var which = isNew ? "new password" : "password";
        // A password that starts with a digit is stored with a Q in front of it.
        var text = password.Length > 0 && char.IsAsciiDigit(password[0]) ? "Q" + password : password;
        if (text.Length == 0) throw new SignOnException(failure, $"Type the {which}.");
        if (text.Length > 10)
            throw new SignOnException(failure, "This IBM i takes passwords of up to 10 characters.");
        var upper = text.ToUpperInvariant();
        if (upper.Any(c => c > 0xff || Ebcdic.Encode(c.ToString())[0] == Ebcdic.Substitute && c != '?'))
            throw new SignOnException(failure, $"The {which} has a character the IBM i can't take.");
        return Ebcdic.Encode(upper.PadRight(10));
    }

    /// <summary>The password token: the user ID enciphered with the password (two halves for 9 or 10 characters).</summary>
    private static byte[] DesToken(byte[] userId, byte[] pwd)
    {
        var id = userId[..8];
        if (BlankLength(userId) > 8)
        {
            // Fold characters 9 and 10 into the first eight, two bits to a byte.
            for (var i = 0; i < 4; i++)
            {
                var shift = 6 - 2 * i;
                id[i] ^= (byte)(((userId[8] >> shift) & 0x03) << 6);
                id[4 + i] ^= (byte)(((userId[9] >> shift) & 0x03) << 6);
            }
        }
        var length = BlankLength(pwd);
        byte[] first = new byte[8], second = new byte[8];
        try
        {
            first.AsSpan().Fill(0x40);
            pwd.AsSpan(0, Math.Min(length, 8)).CopyTo(first);
            var token = Encipher(ShiftKey(first), id);
            if (length <= 8) return token;
            second.AsSpan().Fill(0x40);
            pwd.AsSpan(8, length - 8).CopyTo(second);
            var other = Encipher(ShiftKey(second), id);
            for (var i = 0; i < 8; i++) token[i] ^= other[i];
            CryptographicOperations.ZeroMemory(other);
            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    /// <summary>Each byte XORed with 0x55, then the eight bytes shifted left one bit as a whole.</summary>
    private static byte[] ShiftKey(byte[] block)
    {
        for (var i = 0; i < 8; i++) block[i] ^= 0x55;
        var value = BinaryPrimitives.ReadUInt64BigEndian(block) << 1;
        BinaryPrimitives.WriteUInt64BigEndian(block, value);
        return block;
    }

    /// <summary>A DES MAC over the seeds, the sequence number and the user ID, keyed with the token.</summary>
    private static byte[] DesSubstitute(byte[] userId, byte[] token, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed,
        ReadOnlySpan<byte> sequence)
    {
        // The server seed plus the sequence number, as one 64-bit number.
        var seeded = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seeded,
            BinaryPrimitives.ReadUInt64BigEndian(serverSeed) + BinaryPrimitives.ReadUInt64BigEndian(sequence));

        var block = Encipher(token, seeded);
        Xor(block, clientSeed);
        block = Encipher(token, block);

        var data = new byte[8];
        for (var i = 0; i < 8; i++) data[i] = (byte)(userId[i] ^ seeded[i] ^ block[i]);
        block = Encipher(token, data);

        data.AsSpan().Fill(0x40);
        data[0] = userId[8];
        data[1] = userId[9];
        for (var i = 0; i < 8; i++) data[i] = (byte)(data[i] ^ seeded[i] ^ block[i]);
        block = Encipher(token, data);

        Xor(block, sequence);
        return Encipher(token, block);
    }

    public static byte[] Sha(string user, string password, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        var id = ShaUserId(user);
        var pwd = ShaPassword(password);
        try
        {
            var token = ShaToken(id, pwd);
            var substitute = ShaSubstitute(token, id, clientSeed, serverSeed, Sequence);
            CryptographicOperations.ZeroMemory(token);
            return substitute;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwd);
        }
    }

    public static byte[] Sha512(string user, string password, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        // The same checks as the other levels; the bytes themselves aren't used for the token.
        CryptographicOperations.ZeroMemory(ShaPassword(password));
        var token = Level4Token(user, password);
        var substitute = ShaSubstitute(token, ShaUserId(user), clientSeed, serverSeed, Sequence, wide: true);
        CryptographicOperations.ZeroMemory(token);
        return substitute;
    }

    /// <summary>
    /// The QPWDLVL 4 password token: PBKDF2 with HMAC-SHA-512, 10022 rounds, 64 bytes, over
    /// the password, salted with SHA-256 of the user ID (upper case, padded to 10) followed by
    /// the password's last four characters (padded with blanks to 4), both as UTF-16
    /// big-endian. The password itself goes in as the Toolbox for Java passes it: as typed
    /// (trailing blanks kept), in UTF-8.
    /// </summary>
    private static byte[] Level4Token(string user, string password)
    {
        var tail = password.Length > 4 ? password[^4..] : password.PadRight(4);
        var saltInput = Encoding.BigEndianUnicode.GetBytes(user.ToUpperInvariant().PadRight(10)[..10] + tail);
        var pwd = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(pwd, SHA256.HashData(saltInput), Level4Iterations, HashAlgorithmName.SHA512, 64);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(saltInput);
            CryptographicOperations.ZeroMemory(pwd);
        }
    }

    /// <summary>The user ID as QPWDLVL 2 and 3 hash it: upper case, UTF-16 big-endian, padded to 10 characters.</summary>
    private static byte[] ShaUserId(string user) => Encoding.BigEndianUnicode.GetBytes(user.ToUpperInvariant().PadRight(10));

    /// <summary>A password as QPWDLVL 2 and 3 hash it: as typed, without trailing blanks, UTF-16 big-endian.</summary>
    private static byte[] ShaPassword(string password, bool isNew = false)
    {
        var failure = isNew ? SignOnFailure.NewPasswordRefused : SignOnFailure.InvalidPassword;
        var which = isNew ? "new password" : "password";
        var text = password.TrimEnd(' ', '\0', '\u3000');
        if (text.Length == 0) throw new SignOnException(failure, $"Type the {which}.");
        if (text[0] == '*') throw new SignOnException(failure, "A password can't start with *.");
        if (text.Length > 128)
            throw new SignOnException(failure, "This IBM i takes passwords of up to 128 characters.");
        return Encoding.BigEndianUnicode.GetBytes(text);
    }

    /// <summary>The password token: SHA-1 over the user ID and the password.</summary>
    private static byte[] ShaToken(byte[] id, byte[] pwd)
    {
        var input = new byte[id.Length + pwd.Length];
        try
        {
            id.CopyTo(input, 0);
            pwd.CopyTo(input, id.Length);
            return SHA1.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>SHA-1 (SHA-512 when `wide`) over the token, both seeds, the user ID and the sequence number.</summary>
    private static byte[] ShaSubstitute(byte[] token, byte[] id, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed,
        ReadOnlySpan<byte> sequence, bool wide = false)
    {
        var input = new byte[token.Length + 8 + 8 + id.Length + 8];
        token.CopyTo(input, 0);
        serverSeed.CopyTo(input.AsSpan(token.Length));
        clientSeed.CopyTo(input.AsSpan(token.Length + 8));
        id.CopyTo(input, token.Length + 16);
        sequence.CopyTo(input.AsSpan(token.Length + 16 + id.Length));
        var substitute = wide ? SHA512.HashData(input) : SHA1.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        return substitute;
    }

    /// <summary>
    /// What a password change sends: the substitute for the old password (sequence 1), then each
    /// password XORed with further substitutes, the new one keyed by the old password's token
    /// and the old one by the new password's, so neither crosses the network in the clear and
    /// the IBM i can check both. The steps follow IBM's Toolbox for Java (AS400ImplRemote:
    /// changePassword, encryptNewPassword, generateShaProtected).
    /// </summary>
    public sealed record Change(byte Scheme, byte[] Substitute, byte[] ProtectedOld, byte[] ProtectedNew, int OldLength, int NewLength);

    public static Change ForChange(int passwordLevel, string user, string oldPassword, string newPassword,
        ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed) => passwordLevel switch
    {
        0 or 1 => DesChange(UserIdEbcdic(user), oldPassword, newPassword, clientSeed, serverSeed),
        2 or 3 => ShaChange(user, oldPassword, newPassword, clientSeed, serverSeed, wide: false),
        4 => ShaChange(user, oldPassword, newPassword, clientSeed, serverSeed, wide: true),
        _ => throw new SignOnException(SignOnFailure.Unsupported,
            $"This IBM i uses password level {passwordLevel}, which GreenGlow can't sign on with."),
    };

    private static Change DesChange(byte[] userId, string oldPassword, string newPassword,
        ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        var oldPwd = DesPassword(oldPassword);
        byte[]? newPwd = null, oldToken = null, newToken = null;
        try
        {
            newPwd = DesPassword(newPassword, isNew: true);
            oldToken = DesToken(userId, oldPwd);
            newToken = DesToken(userId, newPwd);
            var sequence = Sequence.ToArray();
            var substitute = DesSubstitute(userId, oldToken, clientSeed, serverSeed, sequence);
            var protectedNew = DesProtected(newPwd, userId, oldToken, clientSeed, serverSeed, sequence);
            var protectedOld = DesProtected(oldPwd, userId, newToken, clientSeed, serverSeed, sequence);
            return new Change(DesScheme, substitute, protectedOld, protectedNew, 0, 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldPwd);
            if (newPwd is not null) CryptographicOperations.ZeroMemory(newPwd);
            if (oldToken is not null) CryptographicOperations.ZeroMemory(oldToken);
            if (newToken is not null) CryptographicOperations.ZeroMemory(newToken);
        }
    }

    /// <summary>
    /// A 10-byte password XORed with the next substitute: 8 bytes, or 16 when it is longer than
    /// 8, the second half being characters 9 and 10 padded with blanks under the substitute after.
    /// </summary>
    private static byte[] DesProtected(byte[] pwd, byte[] userId, byte[] token, ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed, byte[] sequence)
    {
        var result = new byte[pwd[8] == 0x40 && pwd[9] == 0x40 ? 8 : 16];
        Increment(sequence);
        var mask = DesSubstitute(userId, token, clientSeed, serverSeed, sequence);
        for (var i = 0; i < 8; i++) result[i] = (byte)(mask[i] ^ pwd[i]);
        if (result.Length == 16)
        {
            Increment(sequence);
            mask = DesSubstitute(userId, token, clientSeed, serverSeed, sequence);
            for (var i = 0; i < 8; i++) result[8 + i] = (byte)(mask[i] ^ (i < 2 ? pwd[8 + i] : 0x40));
        }
        return result;
    }

    /// <param name="wide">QPWDLVL 4: SHA-512 substitutes over PBKDF2 tokens, in 64-byte blocks, in place of SHA-1's 20.</param>
    private static Change ShaChange(string user, string oldPassword, string newPassword,
        ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed, bool wide)
    {
        var id = ShaUserId(user);
        var oldPwd = ShaPassword(oldPassword);
        byte[]? newPwd = null, oldToken = null, newToken = null;
        try
        {
            newPwd = ShaPassword(newPassword, isNew: true);
            oldToken = wide ? Level4Token(user, oldPassword) : ShaToken(id, oldPwd);
            newToken = wide ? Level4Token(user, newPassword) : ShaToken(id, newPwd);
            var sequence = Sequence.ToArray();
            var substitute = ShaSubstitute(oldToken, id, clientSeed, serverSeed, sequence, wide);
            var protectedNew = ShaProtected(newPwd, oldToken, id, clientSeed, serverSeed, sequence, wide);
            var protectedOld = ShaProtected(oldPwd, newToken, id, clientSeed, serverSeed, sequence, wide);
            return new Change(wide ? Sha512Scheme : ShaScheme, substitute, protectedOld, protectedNew, oldPwd.Length, newPwd.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldPwd);
            if (newPwd is not null) CryptographicOperations.ZeroMemory(newPwd);
            if (oldToken is not null) CryptographicOperations.ZeroMemory(oldToken);
            if (newToken is not null) CryptographicOperations.ZeroMemory(newToken);
        }
    }

    /// <summary>The password XORed with as many substitutes (20 bytes, or 64) as it takes; the rest of the last one follows it.</summary>
    private static byte[] ShaProtected(byte[] pwd, byte[] token, byte[] id, ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed, byte[] sequence, bool wide)
    {
        var block = wide ? 64 : 20;
        var result = new byte[(pwd.Length - 1) / block * block + block];
        for (var at = 0; at < result.Length; at += block)
        {
            Increment(sequence);
            var mask = ShaSubstitute(token, id, clientSeed, serverSeed, sequence, wide);
            for (var i = 0; i < block; i++)
                result[at + i] = at + i < pwd.Length ? (byte)(mask[i] ^ pwd[at + i]) : mask[i];
        }
        return result;
    }

    /// <summary>Adds one to an 8-byte big-endian number.</summary>
    private static void Increment(byte[] sequence) =>
        BinaryPrimitives.WriteUInt64BigEndian(sequence, BinaryPrimitives.ReadUInt64BigEndian(sequence) + 1);

    /// <summary>Single DES on one block. The key's parity bits are ignored, as DES ignores them.</summary>
    internal static byte[] Encipher(byte[] key, byte[] block)
    {
        using var des = DES.Create();
        try
        {
            des.Key = key;
        }
        catch (CryptographicException)
        {
            // One of the 16 weak or semi-weak DES keys: about one chance in 2^52.
            throw new SignOnException(SignOnFailure.Unsupported, "The password substitute could not be built. Try again.");
        }
        return des.EncryptEcb(block, PaddingMode.None);
    }

    private static int BlankLength(byte[] text)
    {
        var n = 0;
        while (n < text.Length && text[n] != 0x40 && text[n] != 0) n++;
        return n;
    }

    private static void Xor(byte[] target, ReadOnlySpan<byte> other)
    {
        for (var i = 0; i < 8; i++) target[i] ^= other[i];
    }
}
