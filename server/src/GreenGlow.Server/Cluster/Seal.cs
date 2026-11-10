using System.Security.Cryptography;
using System.Text;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// Seals the peer link: AES-256-GCM under a key derived from Cluster:Secret (HKDF-SHA256), with
/// what the message is (its path, the nodes, the time; for an answer, the request it answers) as
/// associated data. Without the secret, no one can read, change or send a peer message, and a
/// sealed message is accepted only for what it was sealed for.
/// </summary>
public sealed class Seal
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly byte[] _key;

    public Seal(string secret)
    {
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32,
            "greenglow-connect cluster"u8.ToArray(), "seal v1"u8.ToArray());
    }

    /// <summary>The nonce, the ciphertext and the tag.</summary>
    public byte[] Close(ReadOnlySpan<byte> plain, string context)
    {
        var sealedBytes = new byte[NonceSize + plain.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length),
            Encoding.UTF8.GetBytes(context));
        return sealedBytes;
    }

    /// <summary>The plain text, or null when the message wasn't sealed with this secret for this context.</summary>
    public byte[]? Open(ReadOnlySpan<byte> sealedBytes, string context)
    {
        if (sealedBytes.Length < NonceSize + TagSize) return null;
        var length = sealedBytes.Length - NonceSize - TagSize;
        var plain = new byte[length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedBytes[..NonceSize], sealedBytes.Slice(NonceSize, length), sealedBytes[(NonceSize + length)..], plain,
                Encoding.UTF8.GetBytes(context));
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string NonceOf(ReadOnlySpan<byte> sealedBytes) => Convert.ToBase64String(sealedBytes[..NonceSize]);

    /// <summary>What a request is sealed for: its operation, who sends it to whom, and when (Unix seconds).</summary>
    public static string RequestContext(string op, int from, int to, long time) =>
        $"greenglow-connect request {op} {from}>{to} {time}";

    /// <summary>What an answer is sealed for: the request it answers, by that request's nonce.</summary>
    public static string ResponseContext(string requestNonce) => $"greenglow-connect response {requestNonce}";
}
