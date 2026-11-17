using System.Globalization;
using System.Text;

namespace FxOs.Platform.Identity;

/// <summary>
/// Password rules from the Phase 3 brief: at least 12 characters, with a number and a symbol.
/// The web forms check the same rules live; the API is the authority. Rule codes map to ES/EN catalog keys.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;

    // Upper bound keeps Argon2 input reasonable; long passphrases still fit.
    public const int MaxLength = 128;

    public const string RuleMinLength = "min_length";
    public const string RuleMaxLength = "max_length";
    public const string RuleNumber = "number";
    public const string RuleSymbol = "symbol";

    /// <summary>Not the email or a part of the name (from the prototype's T-10 rules).</summary>
    public const string RulePersonalInfo = "personal_info";

    /// <summary>Returns the rule codes the password breaks; empty when it is acceptable.</summary>
    public static IReadOnlyList<string> Check(string? password) => Check(password, null, null);

    /// <summary>
    /// As <see cref="Check(string?)"/>, plus <see cref="RulePersonalInfo"/>: the password may not contain the email's
    /// local part or any part of the name of three or more letters, ignoring case and accents.
    /// </summary>
    public static IReadOnlyList<string> Check(string? password, string? email, string? displayName)
    {
        var text = (password ?? "").Normalize(NormalizationForm.FormC);

        // Count what a person sees as characters (an emoji or accented letter is one), not UTF-16 units.
        var length = new StringInfo(text).LengthInTextElements;

        var broken = new List<string>(4);
        if (length < MinLength)
        {
            broken.Add(RuleMinLength);
        }

        if (length > MaxLength)
        {
            broken.Add(RuleMaxLength);
        }

        if (!text.Any(char.IsDigit))
        {
            broken.Add(RuleNumber);
        }

        if (!text.Any(IsSymbol))
        {
            broken.Add(RuleSymbol);
        }

        if (ContainsPersonalInfo(text, email, displayName))
        {
            broken.Add(RulePersonalInfo);
        }

        return broken;
    }

    public static bool IsAcceptable(string? password) => Check(password).Count == 0;

    private static bool ContainsPersonalInfo(string password, string? email, string? displayName)
    {
        var haystack = Fold(password);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var local = email.Split('@')[0];
            parts.Add(local);
            parts.AddRange(local.Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries));
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            parts.AddRange(displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return parts.Select(Fold).Any(p => p.Length >= 3 && haystack.Contains(p, StringComparison.Ordinal));
    }

    // Lower case without accents, so "Peña" and "pena" match.
    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var kept = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return new string(kept.ToArray()).ToLowerInvariant();
    }

    // Anything that isn't a letter, digit, space or control character counts: punctuation, math, currency, emoji.
    private static bool IsSymbol(char c) =>
        !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsControl(c);
}
