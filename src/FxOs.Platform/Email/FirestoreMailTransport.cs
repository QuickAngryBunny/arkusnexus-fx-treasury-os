using Google.Cloud.Firestore;
using Grpc.Core;

namespace FxOs.Platform.Email;

/// <summary>One email handed to the transport. <see cref="Id"/> is the outbox id, used as the idempotency key.</summary>
public sealed record OutboundEmail(Guid Id, string To, string Subject, string Html, string? Text);

/// <summary>Hands an email to the provider. Throws on failure; the dispatcher retries.</summary>
public interface IMailTransport
{
    /// <returns>The provider's reference for the email.</returns>
    Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default);
}

/// <summary>
/// Firebase Trigger Email extension: one document per email in the watched collection; the extension sends it from
/// the account configured in Firebase. Firebase never authenticates our users and receives only the email itself.
/// The document id is the outbox id, so a retry after a lost response can't send twice.
/// </summary>
public sealed class FirestoreMailTransport : IMailTransport
{
    private readonly FirebaseSettings _settings;
    private readonly Lazy<FirestoreDb> _db;

    public FirestoreMailTransport(EmailSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Firebase;
        _db = new Lazy<FirestoreDb>(() => new FirestoreDbBuilder
        {
            ProjectId = _settings.ProjectId,
            CredentialsPath = string.IsNullOrWhiteSpace(_settings.CredentialsFile) ? null : _settings.CredentialsFile,
        }.Build());
    }

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var id = email.Id.ToString("N");
        var document = _db.Value.Collection(_settings.Collection).Document(id);
        try
        {
            await document.CreateAsync(BuildDocument(email), ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            // An earlier attempt got through; the extension already has it.
        }

        return document.Path;
    }

    /// <summary>The document shape the Trigger Email extension reads: <c>{ to, message: { subject, html, text } }</c>.</summary>
    public static Dictionary<string, object> BuildDocument(OutboundEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var message = new Dictionary<string, object>
        {
            ["subject"] = email.Subject,
            ["html"] = email.Html,
        };
        if (!string.IsNullOrEmpty(email.Text))
        {
            message["text"] = email.Text;
        }

        return new Dictionary<string, object>
        {
            ["to"] = email.To,
            ["message"] = message,
        };
    }
}
