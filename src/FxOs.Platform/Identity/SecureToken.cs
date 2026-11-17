using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace FxOs.Platform.Identity;

/// <summary>A freshly issued token: the raw value goes in the email link or cookie, only the hash is stored.</summary>
public sealed record IssuedSecret(string Raw, byte[] Hash);

/// <summary>
/// Random 256-bit tokens for invitation links, password resets, email changes and sessions.
/// Raw form is unpadded base64url (43 characters); the database keeps SHA-256 of that string (32 bytes).
/// </summary>
public static class SecureToken
{
    public const int RawLength = 43;
    private const int TokenBytes = 32;

    public static IssuedSecret New()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        var raw = Base64Url.EncodeToString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return new IssuedSecret(raw, Hash(raw));
    }

    /// <summary>Hash of a token from a link or cookie, or null when it can't be one of ours (nothing to look up).</summary>
    public static byte[]? TryHash(string? raw)
    {
        // Exactly what New() produces: unpadded base64url. (Base64Url.IsValid would also accept '=' padding.)
        if (raw is null || raw.Length != RawLength || !raw.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        return Hash(raw);
    }

    private static byte[] Hash(string raw) => SHA256.HashData(Encoding.ASCII.GetBytes(raw));
}
