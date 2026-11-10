using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GreenGlow.Engine;

namespace GreenGlow.Server.Printing;

/// <summary>
/// The password substitute a host server sign-on sends in place of the password, built from
/// both random seeds so it is good for one sign-on only. QPWDLVL 0 and 1 use the DES scheme
/// (passwords of up to 10 characters, case-insensitive), 2 and 3 the SHA-1 scheme (up to 128,
/// case-sensitive). Level 4 exists only from IBM i 7.5 and is not supported. The steps follow
/// IBM's Toolbox for Java (AS400ImplRemote: generateToken, generatePasswordSubstitute,
/// generateShaToken, generateShaSubstitute). Every buffer holding the password is cleared
/// before returning.
/// </summary>
public static class PasswordSubstitute
{
    public const byte DesScheme = 0x01;
    public const byte ShaScheme = 0x03;
    private static readonly byte[] Sequence = [0, 0, 0, 0, 0, 0, 0, 1];

    /// <summary>The scheme byte for Start Server and the substitute, or a SignOnException that sent nothing.</summary>
    public static (byte Scheme, byte[] Substitute) For(int passwordLevel, string user, string password,
        ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed) => passwordLevel switch
    {
        0 or 1 => (DesScheme, Des(UserIdEbcdic(user), password, clientSeed, serverSeed)),
        2 or 3 => (ShaScheme, Sha(user, password, clientSeed, serverSeed)),
        _ => throw new SignOnException(SignOnFailure.Unsupported,
            $"This IBM i uses password level {passwordLevel}, which GreenGlow can't sign on with."),
    };

    /// <summary>The user ID in CCSID 37, upper case, padded with blanks to 10 bytes.</summary>
    public static byte[] UserIdEbcdic(string user) => Ebcdic.Encode(user.ToUpperInvariant().PadRight(10));

    public static byte[] Des(byte[] userId, string password, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        // A password that starts with a digit is stored with a Q in front of it.
        var text = password.Length > 0 && char.IsAsciiDigit(password[0]) ? "Q" + password : password;
        if (text.Length == 0) throw new SignOnException(SignOnFailure.InvalidPassword, "Type the password.");
        if (text.Length > 10)
            throw new SignOnException(SignOnFailure.InvalidPassword, "This IBM i takes passwords of up to 10 characters.");
        var upper = text.ToUpperInvariant();
        if (upper.Any(c => c > 0xff || Ebcdic.Encode(c.ToString())[0] == Ebcdic.Substitute && c != '?'))
            throw new SignOnException(SignOnFailure.InvalidPassword, "The password has a character the IBM i can't take.");
        var pwd = Ebcdic.Encode(upper.PadRight(10));
        try
        {
            var token = DesToken(userId, pwd);
            try
            {
                return DesSubstitute(userId, token, clientSeed, serverSeed);
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
    private static byte[] DesSubstitute(byte[] userId, byte[] token, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        // The server seed plus the sequence number, as one 64-bit number.
        var seeded = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seeded,
            BinaryPrimitives.ReadUInt64BigEndian(serverSeed) + BinaryPrimitives.ReadUInt64BigEndian(Sequence));

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

        Xor(block, Sequence);
        return Encipher(token, block);
    }

    public static byte[] Sha(string user, string password, ReadOnlySpan<byte> clientSeed, ReadOnlySpan<byte> serverSeed)
    {
        var text = password.TrimEnd(' ', '\0', '　');
        if (text.Length == 0) throw new SignOnException(SignOnFailure.InvalidPassword, "Type the password.");
        if (text[0] == '*') throw new SignOnException(SignOnFailure.InvalidPassword, "A password can't start with *.");
        if (text.Length > 128)
            throw new SignOnException(SignOnFailure.InvalidPassword, "This IBM i takes passwords of up to 128 characters.");
        var id = Encoding.BigEndianUnicode.GetBytes(user.ToUpperInvariant().PadRight(10));
        var pwd = Encoding.BigEndianUnicode.GetBytes(text);
        var tokenInput = new byte[id.Length + pwd.Length];
        try
        {
            id.CopyTo(tokenInput, 0);
            pwd.CopyTo(tokenInput, id.Length);
            var token = SHA1.HashData(tokenInput);
            var input = new byte[token.Length + 8 + 8 + id.Length + 8];
            token.CopyTo(input, 0);
            serverSeed.CopyTo(input.AsSpan(20));
            clientSeed.CopyTo(input.AsSpan(28));
            id.CopyTo(input, 36);
            Sequence.CopyTo(input, 36 + id.Length);
            var substitute = SHA1.HashData(input);
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(input);
            return substitute;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwd);
            CryptographicOperations.ZeroMemory(tokenInput);
        }
    }

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
