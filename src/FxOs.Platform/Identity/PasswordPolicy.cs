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

    /// <summary>Returns the rule codes the password breaks; empty when it is acceptable.</summary>
    public static IReadOnlyList<string> Check(string? password)
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

        return broken;
    }

    public static bool IsAcceptable(string? password) => Check(password).Count == 0;

    // Anything that isn't a letter, digit, space or control character counts: punctuation, math, currency, emoji.
    private static bool IsSymbol(char c) =>
        !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsControl(c);
}
