using System.Text.Json;
using System.Text.RegularExpressions;
using FxOs.Platform.Data;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace FxOs.Platform.Email;

/// <summary>An email to send. <see cref="Html"/> may hold raw links; it is stored encrypted and cleared after sending.</summary>
public sealed record EmailMessage(
    string Template, string Locale, string To, string Subject, string Html, string? Text = null, Guid? TenantId = null);

/// <summary>Body as stored (encrypted) in email_outbox.body_protected.</summary>
internal sealed record EmailBody(string Html, string? Text);

/// <summary>
/// Writes emails to the control-plane outbox inside the caller's transaction, so an email exists only if the change
/// that caused it commits. <see cref="EmailDispatcher"/> hands them to Firebase.
/// </summary>
public sealed partial class EmailOutbox(IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "FxOs.EmailOutbox.Body.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector = dataProtection.CreateProtector(ProtectorPurpose);

    public async Task<Guid> EnqueueAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(message);
        if (!TemplateKey().IsMatch(message.Template))
        {
            throw new ArgumentException($"Template key '{message.Template}' is not valid.", nameof(message));
        }

        if (message.Locale is not ("en-US" or "es-MX"))
        {
            throw new ArgumentException($"Locale '{message.Locale}' is not supported.", nameof(message));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message.To);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Html);

        var body = _protector.Protect(JsonSerializer.Serialize(new EmailBody(message.Html, message.Text), Json));
        return (await conn.ScalarAsync<Guid>(
            """
            insert into email_outbox (tenant_id, template, locale, to_email, subject, body_protected)
            values (@tenantId, @template, @locale, @to, @subject, @body)
            returning id
            """,
            new
            {
                tenantId = message.TenantId,
                template = message.Template,
                locale = message.Locale,
                to = message.To.Trim().ToLowerInvariant(),
                subject = message.Subject,
                body,
            },
            tx, ct))!;
    }

    internal EmailBody Unprotect(string protectedBody) =>
        JsonSerializer.Deserialize<EmailBody>(_protector.Unprotect(protectedBody), Json)
        ?? throw new InvalidOperationException("Empty email body.");

    [GeneratedRegex("^[a-z][a-z0-9_]{2,63}$")]
    private static partial Regex TemplateKey();
}
