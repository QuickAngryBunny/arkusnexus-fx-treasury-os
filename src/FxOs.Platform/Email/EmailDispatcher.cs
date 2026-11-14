using Dapper;
using FxOs.Platform.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxOs.Platform.Email;

/// <summary>
/// Moves due outbox rows to the mail transport. Rows are claimed with SKIP LOCKED and a lease, so several dispatchers
/// never send the same email at once and a crashed one's claims come back after the lease. Failures retry with
/// backoff (1, 2, 4… minutes, at most 60) up to MaxAttempts. Once sent or given up, the encrypted body is cleared.
/// </summary>
public sealed class EmailDispatcher(
    ControlPlaneDb controlPlane,
    EmailOutbox outbox,
    IMailTransport transport,
    EmailSettings settings,
    TimeProvider time,
    ILogger<EmailDispatcher> logger)
{
    private const int MaxErrorLength = 1000;

    /// <summary>Processes one batch; returns how many emails were handled (sent, retried or failed).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var dispatcher = settings.Dispatcher;
        var now = time.GetUtcNow();

        IReadOnlyList<ClaimedRow> claimed;
        await using (var conn = await controlPlane.OpenAsync(ct))
        {
            claimed = (await conn.QueryAsync<ClaimedRow>(new CommandDefinition(
                """
                with due as (
                    select id from email_outbox
                    where status in ('pending', 'sending') and next_attempt_at <= @now
                    order by next_attempt_at
                    limit @batch
                    for update skip locked)
                update email_outbox o
                set status = 'sending', attempts = o.attempts + 1, next_attempt_at = @leaseUntil
                from due
                where o.id = due.id
                returning o.id, o.to_email, o.subject, o.body_protected, o.attempts
                """,
                new
                {
                    now = now.UtcDateTime,
                    batch = dispatcher.BatchSize,
                    leaseUntil = (now + TimeSpan.FromSeconds(dispatcher.LeaseSeconds)).UtcDateTime,
                },
                cancellationToken: ct))).AsList();
        }

        foreach (var row in claimed)
        {
            await HandleAsync(row, ct);
        }

        return claimed.Count;
    }

    private async Task HandleAsync(ClaimedRow row, CancellationToken ct)
    {
        if (!settings.IsAllowed(row.ToEmail))
        {
            await FailAsync(row, "Recipient not in Email:AllowedRecipients for this environment; not sent.", final: true, ct);
            return;
        }

        EmailBody body;
        try
        {
            body = outbox.Unprotect(row.BodyProtected!);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
                                       or InvalidOperationException or ArgumentNullException)
        {
            // Keys lost or body corrupted: retrying can't help.
            await FailAsync(row, "Email body could not be decrypted: " + ex.GetType().Name, final: true, ct);
            return;
        }

        string providerRef;
        try
        {
            providerRef = await transport.SendAsync(new OutboundEmail(row.Id, row.ToEmail, row.Subject, body.Html, body.Text), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var final = row.Attempts >= settings.Dispatcher.MaxAttempts;
            logger.LogWarning(ex, "Email {EmailId} attempt {Attempt} failed{Final}", row.Id, row.Attempts, final ? " (giving up)" : "");
            await FailAsync(row, ex.GetType().Name + ": " + ex.Message, final, ct);
            return;
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync(
            """
            update email_outbox
            set status = 'sent', sent_at = @now, provider_ref = @providerRef, body_protected = null, last_error = null
            where id = @id
            """,
            new { now = time.GetUtcNow().UtcDateTime, providerRef, id = row.Id }, ct: ct);
        logger.LogInformation("Email {EmailId} handed to the mail transport", row.Id);
    }

    private async Task FailAsync(ClaimedRow row, string error, bool final, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var backoff = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, row.Attempts - 1))));
        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync(
            final
                ? "update email_outbox set status = 'failed', body_protected = null, last_error = @error where id = @id"
                : "update email_outbox set status = 'pending', next_attempt_at = @retryAt, last_error = @error where id = @id",
            new
            {
                error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error,
                retryAt = (now + backoff).UtcDateTime,
                id = row.Id,
            },
            ct: ct);
        if (final)
        {
            logger.LogError("Email {EmailId} failed and will not be retried: {Error}", row.Id, error);
        }
    }

    private sealed class ClaimedRow
    {
        public Guid Id { get; set; }
        public string ToEmail { get; set; } = "";
        public string Subject { get; set; } = "";
        public string? BodyProtected { get; set; }
        public int Attempts { get; set; }
    }
}

/// <summary>Runs <see cref="EmailDispatcher"/> on a timer. Registered only when Email:Dispatcher:Enabled and Firebase are set.</summary>
public sealed class EmailDispatcherService(EmailDispatcher dispatcher, EmailSettings settings, ILogger<EmailDispatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.Dispatcher.PollSeconds)));
        do
        {
            try
            {
                // Drain: keep going while full batches come back.
                while (await dispatcher.RunOnceAsync(stoppingToken) >= settings.Dispatcher.BatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Email dispatcher pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
