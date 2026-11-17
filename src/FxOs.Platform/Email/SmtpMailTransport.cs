using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FxOs.Platform.Email;

/// <summary>
/// Sends through an SMTP server (<c>Email:Transport=smtp</c>), e.g. Gmail with an app password. For hosts without
/// Google credentials. SMTP has no idempotency key, so the Message-ID is derived from the outbox id: if a send succeeds
/// but marking it sent fails, the retry carries the same Message-ID and mail clients show it once.
/// </summary>
public sealed class SmtpMailTransport(EmailSettings settings) : IMailTransport
{
    private readonly SmtpSettings _smtp = (settings ?? throw new ArgumentNullException(nameof(settings))).Smtp;

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var message = BuildMessage(email, _smtp);
        using var client = new SmtpClient();
        await client.ConnectAsync(_smtp.Host, _smtp.Port, Security(_smtp.Security), ct);
        if (!string.IsNullOrEmpty(_smtp.Username))
        {
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
        return "smtp:" + message.MessageId;
    }

    /// <summary>The message as sent: from the configured sender, HTML with a plain-text alternative.</summary>
    public static MimeMessage BuildMessage(OutboundEmail email, SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(smtp);
        var message = new MimeMessage
        {
            Subject = email.Subject,
            MessageId = $"{email.Id:N}@fxos",
            Body = new BodyBuilder { HtmlBody = email.Html, TextBody = email.Text }.ToMessageBody(),
        };
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.To));
        return message;
    }

    private static SecureSocketOptions Security(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ssl" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
