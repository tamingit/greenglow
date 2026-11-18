// The fake IBM i's user profiles, shared by its host servers, and the password substitute
// checks. The substitutes are computed their own way (the DES one as a CBC MAC), not with the
// server's code, so each checks the other.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GreenGlow.Engine;

namespace GreenGlow.FakeHost;

/// <summary>The fake IBM i's user profiles: a password each, and which are expired or disabled. Placeholder users only.</summary>
public sealed class FakeAccounts
{
    public const string DefaultUser = "PRTUSER";
    public const string DefaultPassword = "Secret12";

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _passwords = new() { [DefaultUser] = DefaultPassword };
    private readonly HashSet<string> _expired = [];
    private readonly HashSet<string> _disabled = [];

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes, 2 or 3 for SHA-1, 4 for SHA-512.</summary>
    public int PasswordLevel { get; set; } = 2;

    /// <summary>Adds a user (upper case), or gives one a new password.</summary>
    public void Set(string user, string password)
    {
        lock (_gate) _passwords[user.ToUpperInvariant()] = password;
    }

    public string? PasswordOf(string user)
    {
        lock (_gate) return _passwords.GetValueOrDefault(user.ToUpperInvariant());
    }

    /// <summary>The user's password has expired: sign-on answers 0003000D until it is changed.</summary>
    public void Expire(string user)
    {
        lock (_gate) _expired.Add(user.ToUpperInvariant());
    }

    public void Disable(string user)
    {
        lock (_gate) _disabled.Add(user.ToUpperInvariant());
    }

    /// <summary>The return code for a sign-on with a password substitute (sequence 1): 0 when it is right.</summary>
    public int Check(string user, byte scheme, int level, byte[] substitute, byte[] clientSeed, byte[] serverSeed)
    {
        lock (_gate)
        {
            if (!_passwords.TryGetValue(user, out var password)) return 0x00020001;
            if (_disabled.Contains(user)) return 0x00020002;
            if (scheme != FakeCrypto.Scheme(level)) return 0x00010009;
            if (!substitute.AsSpan().SequenceEqual(FakeCrypto.Substitute(level, user, password, clientSeed, serverSeed)))
                return 0x0003000B;
            return _expired.Contains(user) ? 0x0003000D : 0;
        }
    }

    /// <summary>
    /// A password change: checks the old password's substitute, recovers the new password
    /// from under the old password's substitutes, checks the old one sent under the new
    /// password's, then applies it. The new password must differ from the old one (00030006)
    /// and have at least six characters (00030002).
    /// </summary>
    public int Change(string user, byte scheme, int level, byte[] substitute, byte[] protectedOld, byte[] protectedNew,
        int oldLength, int newLength, byte[] clientSeed, byte[] serverSeed)
    {
        lock (_gate)
        {
            if (!_passwords.TryGetValue(user, out var password)) return 0x00020001;
            if (_disabled.Contains(user)) return 0x00020002;
            if (scheme != FakeCrypto.Scheme(level)) return 0x00010009;
            if (!substitute.AsSpan().SequenceEqual(FakeCrypto.Substitute(level, user, password, clientSeed, serverSeed)))
                return 0x0003000B;

            ulong sequence = 1;
            string newPassword;
            if (level < 2)
            {
                if (protectedNew.Length is not (8 or 16) || protectedOld.Length is not (8 or 16)) return 0x0001000B;
                var text = Unmask(protectedNew, 8, n => FakeCrypto.Substitute(level, user, password, clientSeed, serverSeed, n),
                    ref sequence);
                newPassword = Ebcdic.Decode(text.Length == 16 ? [.. text[..8], text[8], text[9]] : text).TrimEnd();
                var old = Unmask(protectedOld, 8, n => FakeCrypto.Substitute(level, user, newPassword, clientSeed, serverSeed, n),
                    ref sequence);
                var stored = password.ToUpperInvariant();
                if (char.IsAsciiDigit(stored[0])) stored = "Q" + stored;
                if (Ebcdic.Decode(old.Length == 16 ? [.. old[..8], old[8], old[9]] : old).TrimEnd() != stored) return 0x0001000C;
                // The Q in front of a leading digit is how it is stored, not what is typed.
                if (newPassword.Length > 1 && newPassword[0] == 'Q' && char.IsAsciiDigit(newPassword[1])) newPassword = newPassword[1..];
                if (string.Equals(newPassword, password, StringComparison.OrdinalIgnoreCase)) return 0x00030006;
            }
            else
            {
                var block = level < 4 ? 20 : 64;
                if (newLength < 2 || newLength > protectedNew.Length || oldLength < 2 || oldLength > protectedOld.Length
                    || protectedNew.Length % block != 0 || protectedOld.Length % block != 0)
                    return 0x0001000B;
                var text = Unmask(protectedNew, block, n => FakeCrypto.Substitute(level, user, password, clientSeed, serverSeed, n),
                    ref sequence);
                newPassword = Encoding.BigEndianUnicode.GetString(text, 0, newLength);
                var old = Unmask(protectedOld, block, n => FakeCrypto.Substitute(level, user, newPassword, clientSeed, serverSeed, n),
                    ref sequence);
                if (Encoding.BigEndianUnicode.GetString(old, 0, oldLength) != password) return 0x0001000C;
                if (newPassword == password) return 0x00030006;
            }
            if (newPassword.Length < 6) return 0x00030002;
            _passwords[user] = newPassword;
            _expired.Remove(user);
            return 0;
        }
    }

    /// <summary>XORs each block with the substitute for the next sequence number.</summary>
    private static byte[] Unmask(byte[] data, int block, Func<ulong, byte[]> substitute, ref ulong sequence)
    {
        var clear = new byte[data.Length];
        for (var at = 0; at < data.Length; at += block)
        {
            var mask = substitute(++sequence);
            for (var i = 0; i < block; i++) clear[at + i] = (byte)(data[at + i] ^ mask[i]);
        }
        return clear;
    }
}

/// <summary>
/// Password substitutes, the fake IBM i's way: a DES CBC MAC for QPWDLVL 0 and 1, SHA-1 for 2
/// and 3, and for 4 SHA-512 over a PBKDF2 token.
/// </summary>
public static class FakeCrypto
{
    /// <summary>The scheme byte a client must send at a password level.</summary>
    public static byte Scheme(int level) => level < 2 ? (byte)0x01 : level < 4 ? (byte)0x03 : (byte)0x07;

    public static byte[] Substitute(int level, string user, string password, byte[] clientSeed, byte[] serverSeed, ulong sequence = 1)
    {
        var id = user.PadRight(10);
        var sequenceBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(sequenceBytes, sequence);
        if (level >= 4)
        {
            // The token: PBKDF2-HMAC-SHA-512 of the password, salted with SHA-256 of the user ID
            // and the password's last four characters.
            var idBytes = Encoding.BigEndianUnicode.GetBytes(id);
            var tail = password.Length >= 4 ? password.Substring(password.Length - 4) : password + new string(' ', 4 - password.Length);
            var token = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
                SHA256.HashData([.. idBytes, .. Encoding.BigEndianUnicode.GetBytes(tail)]), 10022, HashAlgorithmName.SHA512, 64);
            return SHA512.HashData([.. token, .. serverSeed, .. clientSeed, .. idBytes, .. sequenceBytes]);
        }
        if (level >= 2)
        {
            var idBytes = Encoding.BigEndianUnicode.GetBytes(id);
            var token = SHA1.HashData([.. idBytes, .. Encoding.BigEndianUnicode.GetBytes(password)]);
            return SHA1.HashData([.. token, .. serverSeed, .. clientSeed, .. idBytes, .. sequenceBytes]);
        }

        // DES: the token enciphers the (folded) user ID under the password, shifted; the
        // substitute is a CBC MAC under the token over five blocks.
        var userId = Ebcdic.Encode(id);
        var upper = password.ToUpperInvariant();
        if (char.IsAsciiDigit(upper[0])) upper = "Q" + upper;
        var pwd = Ebcdic.Encode(upper.PadRight(10));
        var folded = Fold(userId);
        var length = Array.FindIndex(pwd, b => b == 0x40) is var n and >= 0 ? n : 10;
        var desToken = Des(Key(pwd.AsSpan(0, Math.Min(8, length))), folded);
        if (length > 8)
        {
            var second = Des(Key(pwd.AsSpan(8, length - 8)), folded);
            desToken = BitConverter.GetBytes(BitConverter.ToUInt64(desToken) ^ BitConverter.ToUInt64(second));
        }
        var rdr = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(rdr, BinaryPrimitives.ReadUInt64BigEndian(serverSeed) + sequence);
        byte[] blocks =
        [
            .. rdr,
            .. clientSeed,
            .. userId.Take(8).Select((b, i) => (byte)(b ^ rdr[i])),
            .. new byte[] { userId[8], userId[9], 0x40, 0x40, 0x40, 0x40, 0x40, 0x40 }.Select((b, i) => (byte)(b ^ rdr[i])),
            .. sequenceBytes,
        ];
        using var des = DES.Create();
        des.Key = desToken;
        return des.EncryptCbc(blocks, new byte[8], PaddingMode.None)[^8..];
    }

    /// <summary>Characters 9 and 10 of a long user ID, two bits at a time, into the top of bytes 1 to 8.</summary>
    private static byte[] Fold(byte[] userId)
    {
        var folded = userId[..8];
        if (userId[8] == 0x40) return folded;
        for (var bit = 0; bit < 16; bit += 2)
        {
            var source = userId[8 + bit / 8];
            var pair = (source >> (6 - bit % 8)) & 0x03;
            folded[bit / 2] ^= (byte)(pair << 6);
        }
        return folded;
    }

    private static byte[] Key(ReadOnlySpan<byte> part)
    {
        var block = new byte[8];
        block.AsSpan().Fill(0x40);
        part.CopyTo(block);
        var value = (BinaryPrimitives.ReadUInt64BigEndian(block) ^ 0x5555555555555555UL) << 1;
        BinaryPrimitives.WriteUInt64BigEndian(block, value);
        return block;
    }

    private static byte[] Des(byte[] key, byte[] block)
    {
        using var des = DES.Create();
        des.Key = key;
        return des.EncryptEcb(block, PaddingMode.None);
    }
}
