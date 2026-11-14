using FxOs.Platform.Email;

namespace FxOs.Api.Tests;

/// <summary>Sprint 2 step 3: the nine Phase 3 emails in English and Spanish.</summary>
public sealed class EmailTemplateTests
{
    private const string Evil = "Ana <script>alert(1)</script> & Co";
    private static readonly Uri Link = new("http://localhost:3000/join?token=abc_DEF-123");
    private static readonly DateTimeOffset At = new(2026, 11, 14, 17, 42, 0, TimeSpan.Zero);
    private static readonly EmailClock Tijuana = new("America/Tijuana");
    private static readonly Guid Tenant = Guid.NewGuid();

    public static TheoryData<string, string> AllTemplates()
    {
        var data = new TheoryData<string, string>();
        foreach (var locale in new[] { "en-US", "es-MX" })
        {
            foreach (var template in Builders.Keys)
            {
                data.Add(template, locale);
            }
        }

        return data;
    }

    private static readonly Dictionary<string, Func<string, string, EmailMessage>> Builders = new()
    {
        [EmailTemplate.TenantSetupInvitation] = (l, to) => EmailTemplates.TenantSetupInvitation(l, to,
            new TenantSetupInvitationModel(Tenant, Evil, Evil, Link, At.AddDays(7), Tijuana)),
        [EmailTemplate.TeammateInvitation] = (l, to) => EmailTemplates.TeammateInvitation(l, to,
            new TeammateInvitationModel(Tenant, Evil, Evil, Evil, "approver", Evil, Link, At.AddDays(7), Tijuana)),
        [EmailTemplate.Welcome] = (l, to) => EmailTemplates.Welcome(l, to, new WelcomeModel(Tenant, Evil, Evil, Link)),
        [EmailTemplate.MemberJoined] = (l, to) => EmailTemplates.MemberJoined(l, to,
            new MemberJoinedModel(Tenant, Evil, Evil, "mariana@example.test", Evil, "standard", Link)),
        [EmailTemplate.PasswordReset] = (l, to) => EmailTemplates.PasswordReset(l, to,
            new PasswordResetModel(Tenant, Evil, to, Link, At, Tijuana)),
        [EmailTemplate.PasswordChanged] = (l, to) => EmailTemplates.PasswordChanged(l, to,
            new PasswordChangedModel(Tenant, Evil, to, At, Tijuana, Link)),
        [EmailTemplate.EmailChangeConfirmation] = (l, to) => EmailTemplates.EmailChangeConfirmation(l, to,
            new EmailChangeConfirmationModel(Evil, "new@example.test", Link, At.AddHours(24), Tijuana)),
        [EmailTemplate.RoleChanged] = (l, to) => EmailTemplates.RoleChanged(l, to,
            new RoleChangedModel(Tenant, Evil, Evil, "admin", Evil, Link)),
        [EmailTemplate.AccountDeactivated] = (l, to) => EmailTemplates.AccountDeactivated(l, to,
            new AccountDeactivatedModel(Tenant, Evil, Evil, Evil)),
    };

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Every_template_renders_in_both_languages_with_values_escaped(string template, string locale)
    {
        var message = Builders[template](locale, "ana@example.test");

        Assert.Equal(template, message.Template);
        Assert.Equal(locale, message.Locale);
        Assert.Equal("ana@example.test", message.To);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));
        Assert.False(string.IsNullOrWhiteSpace(message.Text));

        // Names are text, never markup.
        Assert.DoesNotContain("<script>", message.Html, StringComparison.Ordinal);

        if (template != EmailTemplate.AccountDeactivated)
        {
            Assert.Contains("href=\"http://localhost:3000/join?token=abc_DEF-123\"", message.Html, StringComparison.Ordinal);
            Assert.Contains(Link.AbsoluteUri, message.Text!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Values_shown_in_full_are_html_encoded()
    {
        var message = Builders[EmailTemplate.TenantSetupInvitation]("en-US", "ana@example.test");
        Assert.Contains("Set up Ana &lt;script&gt;alert(1)&lt;/script&gt; &amp; Co on FX Intelligence", message.Html, StringComparison.Ordinal);
        Assert.Contains("Set up Ana <script>alert(1)</script> & Co on FX Intelligence", message.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Spanish_and_English_copy_differ()
    {
        var en = EmailTemplates.PasswordReset("en-US", "ana@example.test", new PasswordResetModel(null, "Ana Ruiz", "ana@example.test", Link, At, Tijuana));
        var es = EmailTemplates.PasswordReset("es-MX", "ana@example.test", new PasswordResetModel(null, "Ana Ruiz", "ana@example.test", Link, At, Tijuana));

        Assert.Equal("Reset your FX Intelligence password", en.Subject);
        Assert.Equal("Restablece tu contraseña de FX Intelligence", es.Subject);
        Assert.Contains("Hi Ana,", en.Html, StringComparison.Ordinal);
        Assert.Contains("Hola, Ana:", es.Html, StringComparison.Ordinal);
        Assert.Contains("expires in 1 hour", en.Text!, StringComparison.Ordinal);
        Assert.Contains("vence en 1 hora", es.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_are_shown_in_the_readers_time_zone()
    {
        // 17:42 UTC in November is 09:42 in Tijuana (PST, GMT-08:00).
        var message = EmailTemplates.PasswordReset("en-US", "ana@example.test",
            new PasswordResetModel(null, "Ana", "ana@example.test", Link, At, Tijuana));
        Assert.Contains("14 Nov 2026 at 09:42 (GMT-08:00)", message.Html, StringComparison.Ordinal);

        var unknownZone = EmailTemplates.PasswordReset("en-US", "ana@example.test",
            new PasswordResetModel(null, "Ana", "ana@example.test", Link, At, new EmailClock("Not/AZone")));
        Assert.Contains("17:42 (GMT+00:00)", unknownZone.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_invitation_note_is_quoted_only_when_given()
    {
        var withNote = EmailTemplates.TeammateInvitation("en-US", "m@example.test",
            new TeammateInvitationModel(Tenant, "Mariana", "Ana Ruiz", "Arkus Nexus", "approver", "You’ll approve our plans.", Link, At, Tijuana));
        var without = EmailTemplates.TeammateInvitation("en-US", "m@example.test",
            new TeammateInvitationModel(Tenant, "Mariana", "Ana Ruiz", "Arkus Nexus", "approver", "  ", Link, At, Tijuana));

        Assert.Equal("Ana Ruiz invited you to Arkus Nexus on FX Intelligence", withNote.Subject);
        Assert.Contains("“You’ll approve our plans.” — Ana Ruiz", withNote.Text!, StringComparison.Ordinal);
        Assert.Contains("<strong>Approver</strong>", withNote.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("—", without.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Buttons_only_take_web_links()
    {
        Assert.Throws<ArgumentException>(() => EmailTemplates.Welcome("en-US", "a@example.test",
            new WelcomeModel(Tenant, "Ana", "Arkus", new Uri("javascript:alert(1)"))));
    }
}
