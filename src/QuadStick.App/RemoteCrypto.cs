using System.Security.Cryptography;
using System.Text;

namespace QuadStick.App;

/// <summary>The pairing code and the frame cipher for a clinician session.
/// docs/specs/2026-11-04-remote-session.md is the contract, and
/// docs/specs/remote-vectors.json pins it byte for byte, so the browser side and
/// this side cannot drift apart without a test going red.</summary>
/// <remarks>
/// The relay never sees the code. It sees the room, which is a hash of it, and
/// frames sealed with a key stretched from it. AES-GCM with a random 96 bit
/// nonce per frame: a session is two hours and a few thousand frames at most,
/// far below the point where a random nonce could repeat.
/// </remarks>
public static class RemoteCrypto
{
    // Crockford base32: no I, L, O or U, so a code read aloud cannot be misheard.
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int CodeLength = 12;
    const int NonceBytes = 12;
    const int TagBytes = 16;
    const int Iterations = 600_000;

    /// <summary>12 characters, 60 bits, from the OS random source.</summary>
    public static string NewCode()
    {
        var c = new char[CodeLength];
        for (int i = 0; i < c.Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c);
    }

    /// <summary>K7QM-4XPA-9RTD: three groups of four, the way it is read out.</summary>
    public static string Display(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";

    /// <summary>A code as somebody typed it, made canonical, or null when it is
    /// not a code at all.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;
        var sb = new StringBuilder(CodeLength);
        foreach (var ch in typed.ToUpperInvariant())
        {
            if (ch is '-' or ' ') continue;
            var c = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            if (Alphabet.IndexOf(c) < 0) return null;
            sb.Append(c);
        }
        return sb.Length == CodeLength ? sb.ToString() : null;
    }

    /// <summary>One PBKDF2-HMAC-SHA256 run, 600000 rounds, 48 bytes: the room is
    /// the hex of the first 16, the AES-256 key the last 32. The room comes out
    /// of the same slow function as the key, so a relay that sees room ids gets
    /// no cheaper way to guess the code than an attacker holding a frame.</summary>
    public static (string Room, byte[] Key) Derive(string code)
    {
        var bits = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes("qcm-session-v2"),
            Iterations, HashAlgorithmName.SHA256, 48);
        return (Convert.ToHexString(bits, 0, 16).ToLowerInvariant(), bits[16..]);
    }

    /// <summary>base64url(nonce || ciphertext || tag), AAD = room. The nonce is
    /// random unless a test passes one to reproduce a vector.</summary>
    public static string Seal(byte[] key, string room, string plaintext, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var frame = new byte[NonceBytes + plain.Length + TagBytes];
        nonce.CopyTo(frame, 0);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, frame.AsSpan(NonceBytes, plain.Length),
            frame.AsSpan(NonceBytes + plain.Length), Encoding.UTF8.GetBytes(room));
        return ToBase64Url(frame);
    }

    /// <summary>The plaintext, or null for anything that does not decrypt:
    /// wrong code, a flipped bit, a frame from another room, not base64 at all.</summary>
    public static string? Open(byte[] key, string room, string frame)
    {
        try
        {
            var bytes = FromBase64Url(frame);
            if (bytes.Length < NonceBytes + TagBytes) return null;
            int n = bytes.Length - NonceBytes - TagBytes;
            var plain = new byte[n];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes, n),
                bytes.AsSpan(NonceBytes + n), plain, Encoding.UTF8.GetBytes(room));
            return new UTF8Encoding(false, true).GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>Hex SHA-256 of a profile's text: the rev the two sides compare
    /// so a change is never applied over a file that moved underneath it.</summary>
    public static string Rev(string csv) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))).ToLowerInvariant();

    static string ToBase64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromBase64Url(string s)
    {
        if (s.Contains('+') || s.Contains('/') || s.Contains('=')) throw new FormatException();
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }
}
