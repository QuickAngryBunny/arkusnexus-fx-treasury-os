using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace FxOs.Platform.Identity;

public enum PasswordVerification
{
    Failed,
    Succeeded,

    /// <summary>The password is right but was hashed with older parameters; store a new hash.</summary>
    SucceededRehashNeeded,
}

public interface IPasswordHasher
{
    /// <summary>Returns a PHC string: <c>$argon2id$v=19$m=…,t=…,p=…$salt$hash</c> (unpadded base64).</summary>
    string Hash(string password);

    PasswordVerification Verify(string password, string phcHash);

    /// <summary>
    /// Spends the same time as a real verification and always fails. Call it when the email is unknown so response
    /// time doesn't reveal which accounts exist.
    /// </summary>
    void VerifyAgainstDummy(string password);
}

/// <summary>Argon2id parameters. Defaults follow the OWASP Password Storage Cheat Sheet (19 MiB, 2 passes, 1 lane).</summary>
public sealed record Argon2idParameters(int MemoryKib = 19_456, int Iterations = 2, int Parallelism = 1)
{
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    public static Argon2idParameters Default { get; } = new();
}

public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const string Prefix = "$argon2id$v=19$";

    // Refuse to work on absurd stored parameters (a tampered row could otherwise pin the CPU or memory).
    private const int MaxMemoryKib = 1_048_576;
    private const int MaxIterations = 16;
    private const int MaxParallelism = 16;

    private readonly Argon2idParameters _parameters;
    private readonly Lazy<string> _dummyHash;

    public Argon2idPasswordHasher()
        : this(Argon2idParameters.Default)
    {
    }

    public Argon2idPasswordHasher(Argon2idParameters parameters)
    {
        _parameters = parameters;
        _dummyHash = new Lazy<string>(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));
    }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(Argon2idParameters.SaltBytes);
        var hash = Compute(password, salt, _parameters, Argon2idParameters.HashBytes);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}m={_parameters.MemoryKib},t={_parameters.Iterations},p={_parameters.Parallelism}${B64(salt)}${B64(hash)}");
    }

    public PasswordVerification Verify(string password, string phcHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(phcHash, out var stored, out var salt, out var expected))
        {
            return PasswordVerification.Failed;
        }

        // Argon2 refuses empty input; still spend the time so an empty field answers like a wrong password.
        var empty = password.Length == 0;
        var actual = Compute(empty ? "\0" : password, salt, stored, expected.Length);
        if (empty || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerification.Failed;
        }

        return stored == _parameters && salt.Length == Argon2idParameters.SaltBytes && expected.Length == Argon2idParameters.HashBytes
            ? PasswordVerification.Succeeded
            : PasswordVerification.SucceededRehashNeeded;
    }

    public void VerifyAgainstDummy(string password) => _ = Verify(password ?? "", _dummyHash.Value);

    private static byte[] Compute(string password, byte[] salt, Argon2idParameters p, int length)
    {
        // NFC so "é" typed as one code point or as e + accent gives the same hash.
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon = new Argon2id(bytes)
            {
                Salt = salt,
                MemorySize = p.MemoryKib,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };
            return argon.GetBytes(length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParse(string? phc, out Argon2idParameters parameters, out byte[] salt, out byte[] hash)
    {
        parameters = Argon2idParameters.Default;
        salt = [];
        hash = [];

        if (phc is null || !phc.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // m=…,t=…,p=…$salt$hash
        var parts = phc[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        int? m = null, t = null, p = null;
        foreach (var pair in parts[0].Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (kv[0])
            {
                case "m": m = value; break;
                case "t": t = value; break;
                case "p": p = value; break;
                default: return false;
            }
        }

        if (m is not (>= 8 and <= MaxMemoryKib) || t is not (>= 1 and <= MaxIterations) || p is not (>= 1 and <= MaxParallelism)
            || m < 8 * p)
        {
            return false;
        }

        if (!TryB64(parts[1], out salt) || salt.Length < 8 || !TryB64(parts[2], out hash) || hash.Length is < 16 or > 64)
        {
            return false;
        }

        parameters = new Argon2idParameters(m.Value, t.Value, p.Value);
        return true;
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryB64(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        var padded = text + new string('=', (4 - text.Length % 4) % 4);
        var buffer = new byte[padded.Length * 3 / 4];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
