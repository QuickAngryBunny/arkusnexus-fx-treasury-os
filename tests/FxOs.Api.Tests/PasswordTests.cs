using FxOs.Platform.Identity;

namespace FxOs.Api.Tests;

public sealed class PasswordHasherTests
{
    // Reference values computed with OpenSSL 3.5 (`openssl kdf ... ARGON2ID`), an independent Argon2id implementation:
    // password "password", salt "somesaltsomesalt".
    private const string OpenSslDefaultParameters =
        "$argon2id$v=19$m=19456,t=2,p=1$c29tZXNhbHRzb21lc2FsdA$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE";

    private const string OpenSslOlderParameters =
        "$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHRzb21lc2FsdA$gduXp+Z6iReEolmbyHn5V8s1EtJzmEvZfYoY/Fn/AeI";

    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Matches_an_independent_argon2id_implementation()
    {
        Assert.Equal(PasswordVerification.Succeeded, _hasher.Verify("password", OpenSslDefaultParameters));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("Password", OpenSslDefaultParameters));
    }

    [Fact]
    public void Hashes_are_phc_strings_with_the_owasp_parameters_and_a_fresh_salt()
    {
        var a = _hasher.Hash("Tipo-de-cambio 2026!");
        var b = _hasher.Hash("Tipo-de-cambio 2026!");

        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("Tipo-de-cambio", a, StringComparison.Ordinal);
        Assert.Equal(PasswordVerification.Succeeded, _hasher.Verify("Tipo-de-cambio 2026!", a));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("Tipo-de-cambio 2026?", a));
    }

    [Fact]
    public void Older_parameters_still_verify_and_ask_for_a_rehash()
    {
        Assert.Equal(PasswordVerification.SucceededRehashNeeded, _hasher.Verify("password", OpenSslOlderParameters));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("wrong", OpenSslOlderParameters));
    }

    [Fact]
    public void Accented_text_verifies_whichever_way_it_was_typed()
    {
        var composed = "Contraseña segura 1!";
        var decomposed = "Contraseña segura 1!";
        Assert.Equal(PasswordVerification.Succeeded, _hasher.Verify(decomposed, _hasher.Hash(composed)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("$2b$12$abcdefghijklmnopqrstuuJ8D1aJ0iGq9V8Z1nN2wQ8m0Xr9yZ0e")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$c29tZXNhbHRzb21lc2FsdA$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$c29tZXNhbHRzb21lc2FsdA")]
    [InlineData("$argon2id$v=19$m=19456,t=2$c29tZXNhbHRzb21lc2FsdA$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE")]
    [InlineData("$argon2id$v=19$m=99999999,t=2,p=1$c29tZXNhbHRzb21lc2FsdA$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE")]
    [InlineData("$argon2id$v=19$m=19456,t=999,p=1$c29tZXNhbHRzb21lc2FsdA$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$not*base64$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$c29tZXNhbHRzb21lc2FsdA==$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE")]
    public void Malformed_or_foreign_hashes_fail_without_throwing(string stored)
    {
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("password", stored));
    }

    [Fact]
    public void Dummy_verification_runs_without_error()
    {
        _hasher.VerifyAgainstDummy("anything");
        _hasher.VerifyAgainstDummy(null!);
    }

    [Fact]
    public void An_empty_password_fails_and_cannot_be_hashed()
    {
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("", OpenSslDefaultParameters));
        Assert.Throws<ArgumentException>(() => _hasher.Hash(""));
    }
}

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("Tipo-cambio-2026", new string[0])]
    [InlineData("doce letras 1!", new string[0])]
    [InlineData("contraseña ñandú 7#", new string[0])]
    [InlineData("Short1!", new[] { PasswordPolicy.RuleMinLength })]
    [InlineData("NoNumbersHere!!", new[] { PasswordPolicy.RuleNumber })]
    [InlineData("NoSymbols12345", new[] { PasswordPolicy.RuleSymbol })]
    [InlineData("twelve chars 12", new[] { PasswordPolicy.RuleSymbol })]
    [InlineData("", new[] { PasswordPolicy.RuleMinLength, PasswordPolicy.RuleNumber, PasswordPolicy.RuleSymbol })]
    public void Checks_length_number_and_symbol(string password, string[] expected)
    {
        Assert.Equal(expected, PasswordPolicy.Check(password));
    }

    [Fact]
    public void Length_counts_visible_characters_not_utf16_units()
    {
        // 11 visible characters (13 UTF-16 units: each emoji is a surrogate pair) → too short.
        Assert.Contains(PasswordPolicy.RuleMinLength, PasswordPolicy.Check("abc1defgh😀😀"));
        // Decomposed accents normalise to one character each.
        Assert.Contains(PasswordPolicy.RuleMinLength, PasswordPolicy.Check("ññññññ 1!"));
    }

    [Fact]
    public void Very_long_passwords_are_refused()
    {
        var tooLong = new string('a', PasswordPolicy.MaxLength) + "1!";
        Assert.Equal([PasswordPolicy.RuleMaxLength], PasswordPolicy.Check(tooLong));
        Assert.True(PasswordPolicy.IsAcceptable(new string('a', PasswordPolicy.MaxLength - 2) + "1!"));
    }

    [Fact]
    public void Null_is_treated_as_empty()
    {
        Assert.False(PasswordPolicy.IsAcceptable(null));
    }
}
