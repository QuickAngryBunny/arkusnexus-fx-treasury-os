using System.Globalization;
using static FxOs.Platform.Email.EmailLayout;

namespace FxOs.Platform.Email;

/// <summary>Template keys stored in email_outbox.template.</summary>
public static class EmailTemplate
{
    public const string TenantSetupInvitation = "tenant_setup_invitation";
    public const string TeammateInvitation = "teammate_invitation";
    public const string Welcome = "welcome";
    public const string MemberJoined = "member_joined";
    public const string PasswordReset = "password_reset";
    public const string PasswordChanged = "password_changed";
    public const string EmailChangeConfirmation = "email_change_confirmation";
    public const string RoleChanged = "role_changed";
    public const string AccountDeactivated = "account_deactivated";
}

/// <summary>Times in emails are shown in the reader's time zone with its abbreviation, e.g. "14 Nov 2026, 09:42 (GMT-08:00)".</summary>
public sealed record EmailClock(string TimeZone)
{
    public static EmailClock MexicoCity { get; } = new("America/Mexico_City");

    internal TimeZoneInfo Zone
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Utc;
            }
        }
    }
}

public sealed record TenantSetupInvitationModel(
    Guid TenantId, string AdminName, string Company, Uri Link, DateTimeOffset ExpiresAt, EmailClock Clock);

public sealed record TeammateInvitationModel(
    Guid TenantId, string InviteeName, string InviterName, string Company, string Role, string? Note, Uri Link,
    DateTimeOffset ExpiresAt, EmailClock Clock);

public sealed record WelcomeModel(Guid TenantId, string Name, string Company, Uri AppLink);

public sealed record MemberJoinedModel(
    Guid TenantId, string AdminName, string MemberName, string MemberEmail, string Company, string Role, Uri TeamLink);

public sealed record PasswordResetModel(Guid? TenantId, string Name, string Email, Uri Link, DateTimeOffset RequestedAt, EmailClock Clock);

public sealed record PasswordChangedModel(Guid? TenantId, string Name, string Email, DateTimeOffset ChangedAt, EmailClock Clock, Uri ForgotLink);

public sealed record EmailChangeConfirmationModel(string Name, string NewEmail, Uri Link, DateTimeOffset ExpiresAt, EmailClock Clock);

public sealed record RoleChangedModel(Guid TenantId, string Name, string Company, string NewRole, string ChangedBy, Uri AppLink);

public sealed record AccountDeactivatedModel(Guid TenantId, string Name, string Company, string DeactivatedBy);

/// <summary>
/// The Phase 3 emails in English and Spanish (brief §3). Each returns an <see cref="EmailMessage"/> ready for
/// <see cref="EmailOutbox.EnqueueAsync"/>. Copy follows the prototype screens O-01, T-04 and T-09.
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage TenantSetupInvitation(string locale, string to, TenantSetupInvitationModel m)
    {
        var es = IsEs(locale);
        var layout = new EmailLayout
        {
            Eyebrow = es ? "FX Intelligence · nueva empresa" : "FX Intelligence · new company",
            Title = es ? $"Configura {m.Company} en FX Intelligence" : $"Set up {m.Company} on FX Intelligence",
            Subtitle = es ? "Serás su primer administrador" : "You’ll be its first admin",
            Greeting = Hi(es, m.AdminName),
            Footnote = es
                ? $"Este enlace funciona una vez y vence el {Date(m.ExpiresAt, m.Clock, es)}. Lo recibes porque un administrador de FX Intelligence creó {m.Company} con {to} como primer administrador."
                : $"This link works once and expires on {Date(m.ExpiresAt, m.Clock, es)}. You’re receiving it because an FX Intelligence admin created {m.Company} with {to} as its first admin.",
        };
        layout.Paragraph(Plain(es
                ? $"{m.Company} ya tiene su propio espacio en FX Intelligence, con todo el historial del tipo de cambio USD/MXN cargado. La configuración toma unos 3 minutos: crea tu contraseña, confirma los datos de tu empresa y agrega los bancos con los que conviertes."
                : $"{m.Company} now has its own space on FX Intelligence, with the full USD/MXN rate history already loaded. Setting it up takes about 3 minutes: set your password, confirm your company details and add the banks you convert between."))
            .Bullets(es
                ? ["Consulta el tipo de cambio de hoy, el pronóstico y las noticias que lo mueven", "Planea tus conversiones de pesos con objetivo, rango y probabilidad", "Invita a tu equipo y elige quién aprueba los planes"]
                : ["See today’s rate, the forecast and the news that moves it", "Plan your peso conversions with a target, range and probability", "Invite your team and choose who approves plans"])
            .Button(es ? $"Configurar {m.Company}" : $"Set up {m.Company}", m.Link);
        return Build(EmailTemplate.TenantSetupInvitation, locale, to, m.TenantId,
            es ? $"Te invitaron a configurar {m.Company} en FX Intelligence" : $"You’re invited to set up {m.Company} on FX Intelligence", layout);
    }

    public static EmailMessage TeammateInvitation(string locale, string to, TeammateInvitationModel m)
    {
        var es = IsEs(locale);
        var role = RoleName(m.Role, es);
        var layout = new EmailLayout
        {
            Eyebrow = es ? $"Invitación de {m.InviterName} · {m.Company}" : $"Invitation from {m.InviterName} · {m.Company}",
            Title = es ? $"Únete a {m.Company} en FX Intelligence" : $"Join {m.Company} on FX Intelligence",
            Subtitle = es ? $"Te invitaron como {role}" : $"You’re invited as {role}",
            Greeting = Hi(es, m.InviteeName),
            Footnote = es
                ? $"Este enlace funciona una vez y vence el {Date(m.ExpiresAt, m.Clock, es)}. ¿No lo esperabas? Puedes ignorarlo; no se crea ninguna cuenta hasta que definas una contraseña."
                : $"This link works once and expires on {Date(m.ExpiresAt, m.Clock, es)}. Didn’t expect this? You can ignore it; no account is created until you set a password.",
        };
        if (!string.IsNullOrWhiteSpace(m.Note))
        {
            layout.Quote(m.Note.Trim(), m.InviterName);
        }

        layout.Paragraph(Plain(es
                ? $"FX Intelligence es donde {m.Company} planea sus conversiones de USD a MXN. Tu rol es "
                : $"FX Intelligence is where {m.Company} plans its USD to MXN conversions. Your role is "),
            Strong(role), Plain(". " + RoleDuty(m.Role, es)))
            .Button(es ? "Crea tu contraseña y únete" : "Set your password and join", m.Link);
        return Build(EmailTemplate.TeammateInvitation, locale, to, m.TenantId,
            es ? $"{m.InviterName} te invitó a {m.Company} en FX Intelligence" : $"{m.InviterName} invited you to {m.Company} on FX Intelligence", layout);
    }

    public static EmailMessage Welcome(string locale, string to, WelcomeModel m)
    {
        var es = IsEs(locale);
        var layout = new EmailLayout
        {
            Eyebrow = $"FX Intelligence · {m.Company}",
            Title = es ? $"Te damos la bienvenida a {m.Company}" : $"Welcome to {m.Company}",
            Greeting = Hi(es, m.Name),
        };
        layout.Paragraph(Plain(es
                ? "Tu cuenta está lista. Inicia sesión con tu correo de trabajo y tu contraseña para ver el tipo de cambio de hoy, el pronóstico y los planes de conversión de tu equipo."
                : "Your account is ready. Sign in with your work email and password to see today’s rate, the forecast and your team’s conversion plans."))
            .Button(es ? "Abrir FX Intelligence" : "Open FX Intelligence", m.AppLink);
        return Build(EmailTemplate.Welcome, locale, to, m.TenantId,
            es ? $"Te damos la bienvenida a {m.Company} en FX Intelligence" : $"Welcome to {m.Company} on FX Intelligence", layout);
    }

    public static EmailMessage MemberJoined(string locale, string to, MemberJoinedModel m)
    {
        var es = IsEs(locale);
        var role = RoleName(m.Role, es);
        var layout = new EmailLayout
        {
            Eyebrow = es ? $"{m.Company} · equipo" : $"{m.Company} · team",
            Title = es ? $"{m.MemberName} se unió" : $"{m.MemberName} joined",
            Greeting = Hi(es, m.AdminName),
        };
        layout.Paragraph(Strong(m.MemberName), Plain($" ({m.MemberEmail}) "),
                Plain(es ? $"aceptó tu invitación y se unió a {m.Company} como {role}." : $"accepted your invitation and joined {m.Company} as {role}."))
            .Button(es ? "Ver el equipo" : "See the team", m.TeamLink);
        return Build(EmailTemplate.MemberJoined, locale, to, m.TenantId,
            es ? $"{m.MemberName} se unió a {m.Company}" : $"{m.MemberName} joined {m.Company}", layout);
    }

    public static EmailMessage PasswordReset(string locale, string to, PasswordResetModel m)
    {
        var es = IsEs(locale);
        var layout = new EmailLayout
        {
            Eyebrow = es ? "FX Intelligence · restablecer contraseña" : "FX Intelligence · password reset",
            Title = es ? "Restablece tu contraseña" : "Reset your password",
            Subtitle = es ? $"Solicitado el {DateTime(m.RequestedAt, m.Clock, es)}" : $"Requested {DateTime(m.RequestedAt, m.Clock, es)}",
            Greeting = Hi(es, m.Name),
            Footnote = es
                ? "Este enlace funciona una vez y vence en 1 hora. Si no lo pediste, ignora este correo; tu contraseña sigue igual."
                : "This link works once and expires in 1 hour. If you didn’t ask for it, ignore this email; your password stays the same.",
        };
        layout.Paragraph(Plain(es ? "Alguien pidió restablecer la contraseña de " : "Someone asked to reset the password for "),
                Strong(m.Email), Plain(es ? ". Si fuiste tú, define una nueva abajo." : ". If it was you, set a new one below."))
            .Button(es ? "Definir una contraseña nueva" : "Set a new password", m.Link);
        return Build(EmailTemplate.PasswordReset, locale, to, m.TenantId,
            es ? "Restablece tu contraseña de FX Intelligence" : "Reset your FX Intelligence password", layout);
    }

    public static EmailMessage PasswordChanged(string locale, string to, PasswordChangedModel m)
    {
        var es = IsEs(locale);
        var layout = new EmailLayout
        {
            Eyebrow = es ? "FX Intelligence · seguridad" : "FX Intelligence · security",
            Title = es ? "Tu contraseña cambió" : "Your password was changed",
            Subtitle = es ? $"El {DateTime(m.ChangedAt, m.Clock, es)}" : $"On {DateTime(m.ChangedAt, m.Clock, es)}",
            Greeting = Hi(es, m.Name),
            Footnote = es
                ? "Cerramos tus sesiones en otros dispositivos."
                : "We signed you out on your other devices.",
        };
        layout.Paragraph(Plain(es ? "La contraseña de " : "The password for "), Strong(m.Email),
                Plain(es ? " acaba de cambiar. Si fuiste tú, no tienes que hacer nada." : " was just changed. If it was you, there’s nothing else to do."))
            .Paragraph(Plain(es
                ? "Si no fuiste tú, restablécela ahora y avisa al administrador de tu empresa."
                : "If it wasn’t you, reset it now and tell your company’s admin."))
            .Button(es ? "Restablecer contraseña" : "Reset password", m.ForgotLink);
        return Build(EmailTemplate.PasswordChanged, locale, to, m.TenantId,
            es ? "Tu contraseña de FX Intelligence cambió" : "Your FX Intelligence password was changed", layout);
    }

    public static EmailMessage EmailChangeConfirmation(string locale, string to, EmailChangeConfirmationModel m)
    {
        var es = IsEs(locale);
        var layout = new EmailLayout
        {
            Eyebrow = es ? "FX Intelligence · tu cuenta" : "FX Intelligence · your account",
            Title = es ? "Confirma tu correo nuevo" : "Confirm your new email",
            Greeting = Hi(es, m.Name),
            Footnote = es
                ? $"Este enlace funciona una vez y vence el {DateTime(m.ExpiresAt, m.Clock, es)}. Hasta que lo confirmes, inicias sesión con tu correo actual."
                : $"This link works once and expires {DateTime(m.ExpiresAt, m.Clock, es)}. Until you confirm, you sign in with your current email.",
        };
        layout.Paragraph(Plain(es ? "Pediste usar " : "You asked to use "), Strong(m.NewEmail),
                Plain(es ? " para iniciar sesión en FX Intelligence. Confírmalo para hacer el cambio." : " to sign in to FX Intelligence. Confirm it to make the change."))
            .Button(es ? "Confirmar correo" : "Confirm email", m.Link);
        return Build(EmailTemplate.EmailChangeConfirmation, locale, to, null,
            es ? "Confirma tu correo nuevo para FX Intelligence" : "Confirm your new email for FX Intelligence", layout);
    }

    public static EmailMessage RoleChanged(string locale, string to, RoleChangedModel m)
    {
        var es = IsEs(locale);
        var role = RoleName(m.NewRole, es);
        var layout = new EmailLayout
        {
            Eyebrow = es ? $"{m.Company} · equipo" : $"{m.Company} · team",
            Title = es ? $"Tu rol ahora es {role}" : $"Your role is now {role}",
            Greeting = Hi(es, m.Name),
        };
        layout.Paragraph(Plain(es ? $"{m.ChangedBy} cambió tu rol en {m.Company} a " : $"{m.ChangedBy} changed your role at {m.Company} to "),
                Strong(role), Plain("."))
            .Paragraph(Plain(RoleDuty(m.NewRole, es)))
            .Button(es ? "Abrir FX Intelligence" : "Open FX Intelligence", m.AppLink);
        return Build(EmailTemplate.RoleChanged, locale, to, m.TenantId,
            es ? $"Tu rol en {m.Company} cambió" : $"Your role at {m.Company} changed", layout);
    }

    public static EmailMessage AccountDeactivated(string locale, string to, AccountDeactivatedModel m)
    {
        var es = IsEs(locale);
        var layout = new EmailLayout
        {
            Eyebrow = es ? $"{m.Company} · equipo" : $"{m.Company} · team",
            Title = es ? "Tu acceso fue desactivado" : "Your access was deactivated",
            Greeting = Hi(es, m.Name),
        };
        layout.Paragraph(Plain(es
                ? $"{m.DeactivatedBy} desactivó tu acceso a {m.Company} en FX Intelligence. Ya no puedes iniciar sesión en esa empresa; tu historial se conserva."
                : $"{m.DeactivatedBy} deactivated your access to {m.Company} on FX Intelligence. You can no longer sign in to that company; your history is kept."))
            .Paragraph(Plain(es
                ? "Si crees que es un error, escribe al administrador de tu empresa."
                : "If you think this is a mistake, contact your company’s admin."));
        return Build(EmailTemplate.AccountDeactivated, locale, to, m.TenantId,
            es ? $"Tu acceso a {m.Company} fue desactivado" : $"Your access to {m.Company} was deactivated", layout);
    }

    private static EmailMessage Build(string template, string locale, string to, Guid? tenantId, string subject, EmailLayout layout)
    {
        var (html, text) = layout.Render();
        return new EmailMessage(template, IsEs(locale) ? "es-MX" : "en-US", to, subject, html, text, tenantId);
    }

    private static bool IsEs(string locale) => locale.StartsWith("es", StringComparison.OrdinalIgnoreCase);

    private static string Hi(bool es, string name)
    {
        var first = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return es ? (first.Length > 0 ? $"Hola, {first}:" : "Hola:") : (first.Length > 0 ? $"Hi {first}," : "Hi,");
    }

    private static string RoleName(string role, bool es) => role switch
    {
        "admin" => es ? "Administrador" : "Admin",
        "approver" => es ? "Aprobador" : "Approver",
        _ => es ? "Estándar" : "Standard",
    };

    private static string RoleDuty(string role, bool es) => role switch
    {
        "admin" => es
            ? "Administras tu empresa: el equipo, los roles y los bancos, y también creas planes de conversión."
            : "You manage your company: the team, roles and banks, and you create conversion plans.",
        "approver" => es
            ? "Revisarás los planes de conversión y podrás aprobarlos, pedir cambios o rechazarlos."
            : "You’ll review conversion plans and approve, ask for changes or reject them.",
        _ => es
            ? "Consultas el tipo de cambio y el pronóstico, y creas planes de conversión."
            : "You see the rate and the forecast and create conversion plans.",
    };

    private static CultureInfo Culture(bool es) => CultureInfo.GetCultureInfo(es ? "es-MX" : "en-GB");

    private static string Date(DateTimeOffset at, EmailClock clock, bool es) =>
        TimeZoneInfo.ConvertTime(at, clock.Zone).ToString("d MMM yyyy", Culture(es));

    private static string DateTime(DateTimeOffset at, EmailClock clock, bool es)
    {
        var local = TimeZoneInfo.ConvertTime(at, clock.Zone);
        var offset = local.Offset;
        var gmt = $"GMT{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
        return local.ToString(es ? "d MMM yyyy, HH:mm" : "d MMM yyyy 'at' HH:mm", Culture(es)) + $" ({gmt})";
    }
}
