using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FxOs.Api.Tests;

public sealed class EmailSettingsTests
{
    [Theory]
    [InlineData("ana@example.test", true)]
    [InlineData("  ANA@Example.TEST ", true)]
    [InlineData("anyone@qa.example.test", true)]
    [InlineData("leo@example.test", false)]
    [InlineData("someone@arkusnexus.example", false)]
    [InlineData("qa.example.test", false)]
    public void Only_allowlisted_recipients_are_sent(string to, bool allowed)
    {
        var settings = new EmailSettings { AllowedRecipients = ["ana@example.test", "*@qa.example.test", " "] };
        Assert.Equal(allowed, settings.IsAllowed(to));
    }

    [Fact]
    public void Nothing_is_allowed_by_default()
    {
        Assert.False(new EmailSettings().IsAllowed("ana@example.test"));
        Assert.True(new EmailSettings { AllowAllRecipients = true }.IsAllowed("ana@example.test"));
    }

    [Fact]
    public void Firestore_document_has_the_trigger_email_shape()
    {
        var doc = FirestoreMailTransport.BuildDocument(new OutboundEmail(Guid.NewGuid(), "ana@example.test", "Hola", "<p>Hola</p>", "Hola"));
        Assert.Equal(["to", "message"], doc.Keys);
        Assert.Equal("ana@example.test", doc["to"]);
        var message = Assert.IsType<Dictionary<string, object>>(doc["message"]);
        Assert.Equal(("Hola", "<p>Hola</p>", "Hola"), ((string)message["subject"], (string)message["html"], (string)message["text"]));

        var htmlOnly = FirestoreMailTransport.BuildDocument(new OutboundEmail(Guid.NewGuid(), "a@b.test", "S", "<p>x</p>", null));
        Assert.False(((Dictionary<string, object>)htmlOnly["message"]).ContainsKey("text"));
    }
}

/// <summary>Step 6: outbox (encrypted body, same transaction) → dispatcher → mail transport.</summary>
[Collection(PlatformCollection.Name)]
public sealed class EmailOutboxTests(PlatformFixture fx)
{
    private const string Domain = "@outbox.example.test";

    [Fact]
    public async Task An_email_exists_only_if_its_transaction_commits_and_its_body_is_encrypted()
    {
        var outbox = fx.Services.GetRequiredService<EmailOutbox>();
        var to = NewAddress();
        const string Link = "https://fx.example.test/join?token=RAW-TOKEN-VALUE";

        await using var conn = await OpenAsync();
        await using (var rolledBack = await conn.BeginTransactionAsync())
        {
            await outbox.EnqueueAsync(conn, rolledBack, Message(to, $"<a href=\"{Link}\">Join</a>"));
            await rolledBack.RollbackAsync();
        }

        Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("select count(*) from email_outbox where to_email = @to", new { to }));

        await using (var committed = await conn.BeginTransactionAsync())
        {
            await outbox.EnqueueAsync(conn, committed, Message(to, $"<a href=\"{Link}\">Join</a>"));
            await committed.CommitAsync();
        }

        var row = await conn.QuerySingleAsync<(string Status, string Body)>(
            "select status, body_protected from email_outbox where to_email = @to", new { to });
        Assert.Equal("pending", row.Status);
        Assert.DoesNotContain("RAW-TOKEN-VALUE", row.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Join", row.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_messages_are_refused_before_reaching_the_outbox()
    {
        var outbox = fx.Services.GetRequiredService<EmailOutbox>();
        await using var conn = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.EnqueueAsync(conn, null, Message(NewAddress()) with { Locale = "fr-FR" }));
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.EnqueueAsync(conn, null, Message(NewAddress()) with { Template = "Bad Key" }));
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.EnqueueAsync(conn, null, Message(NewAddress()) with { Html = " " }));
    }

    [Fact]
    public async Task Dispatch_sends_once_marks_sent_and_clears_the_body()
    {
        var transport = new FakeTransport();
        var to = NewAddress();
        var id = await EnqueueAsync(Message(to, "<p>Bienvenida</p>", "Bienvenida"));

        await Dispatcher(transport).RunOnceAsync();
        await Dispatcher(transport).RunOnceAsync();

        var sent = Assert.Single(transport.Sent, e => e.Id == id);
        Assert.Equal((to, "<p>Bienvenida</p>", "Bienvenida"), (sent.To, sent.Html, sent.Text));

        await using var conn = await OpenAsync();
        var row = await conn.QuerySingleAsync<(string Status, string? Body, string? ProviderRef, int Attempts)>(
            "select status, body_protected, provider_ref, attempts from email_outbox where id = @id", new { id });
        Assert.Equal(("sent", (string?)null, $"fake/{id}", 1), row);
    }

    [Fact]
    public async Task Failures_retry_with_backoff_and_then_give_up()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddSeconds(5)); // ahead of the rows' now() defaults
        var transport = new FakeTransport { FailWith = new HttpRequestException("Firestore unavailable") };
        var id = await EnqueueAsync(Message(NewAddress()));
        var dispatcher = Dispatcher(transport, clock, maxAttempts: 3);

        await dispatcher.RunOnceAsync();
        var first = await RowAsync(id);
        Assert.Equal(("pending", 1), (first.Status, first.Attempts));
        Assert.Contains("Firestore unavailable", first.LastError, StringComparison.Ordinal);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(1), first.NextAttemptAt, TimeSpan.FromMilliseconds(1));

        // Not due yet: nothing happens.
        await dispatcher.RunOnceAsync();
        Assert.Equal(1, (await RowAsync(id)).Attempts);

        clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.RunOnceAsync();
        var second = await RowAsync(id);
        Assert.Equal(("pending", 2), (second.Status, second.Attempts));
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(2), second.NextAttemptAt, TimeSpan.FromMilliseconds(1));

        clock.Advance(TimeSpan.FromMinutes(2));
        await dispatcher.RunOnceAsync();
        var final = await RowAsync(id);
        Assert.Equal(("failed", 3, (string?)null), (final.Status, final.Attempts, final.Body));

        // A transient failure followed by success still sends.
        var recovering = new FakeTransport { FailWith = new HttpRequestException("blip"), FailTimes = 1 };
        var id2 = await EnqueueAsync(Message(NewAddress()));
        var again = Dispatcher(recovering, clock);
        await again.RunOnceAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await again.RunOnceAsync();
        Assert.Equal("sent", (await RowAsync(id2)).Status);
    }

    [Fact]
    public async Task Recipients_outside_the_allowlist_never_reach_the_transport()
    {
        var transport = new FakeTransport();
        var id = await EnqueueAsync(Message("real.person@arkusnexus.example"));

        await Dispatcher(transport).RunOnceAsync();

        Assert.DoesNotContain(transport.Sent, e => e.Id == id);
        var row = await RowAsync(id);
        Assert.Equal(("failed", (string?)null), (row.Status, row.Body));
        Assert.Contains("AllowedRecipients", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lapsed_lease_is_picked_up_again()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddSeconds(5)); // ahead of the rows' now() defaults
        var id = await EnqueueAsync(Message(NewAddress()));

        // Simulate a dispatcher that claimed the row and crashed.
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update email_outbox set status = 'sending', attempts = 1, next_attempt_at = @lease where id = @id",
                new { lease = (clock.GetUtcNow() + TimeSpan.FromMinutes(5)).UtcDateTime, id });
        }

        var transport = new FakeTransport();
        await Dispatcher(transport, clock).RunOnceAsync();
        Assert.DoesNotContain(transport.Sent, e => e.Id == id);

        clock.Advance(TimeSpan.FromMinutes(5));
        await Dispatcher(transport, clock).RunOnceAsync();
        Assert.Contains(transport.Sent, e => e.Id == id);
        Assert.Equal(("sent", 2), ((await RowAsync(id)).Status, (await RowAsync(id)).Attempts));
    }

    [Fact]
    public async Task Parallel_dispatchers_send_each_email_once()
    {
        var transport = new FakeTransport { Delay = TimeSpan.FromMilliseconds(20) };
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await EnqueueAsync(Message(NewAddress())));
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Dispatcher(transport, batchSize: 50).RunOnceAsync()));

        foreach (var id in ids)
        {
            Assert.Single(transport.Sent, e => e.Id == id);
        }
    }

    [Fact]
    public async Task Dev_endpoint_queues_a_test_email()
    {
        var to = NewAddress();
        var response = await fx.Client().PostAsJsonAsync("/api/dev/email/test", new { to });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var conn = await OpenAsync();
        Assert.Equal("dev_test", await conn.ExecuteScalarAsync<string>(
            "select template from email_outbox where to_email = @to", new { to }));
    }

    private EmailDispatcher Dispatcher(
        IMailTransport transport, TimeProvider? clock = null, int maxAttempts = 8, int batchSize = 200) =>
        new(fx.Services.GetRequiredService<ControlPlaneDb>(),
            fx.Services.GetRequiredService<EmailOutbox>(),
            transport,
            new EmailSettings
            {
                AllowedRecipients = ["*" + Domain],
                Dispatcher = new DispatcherSettings { MaxAttempts = maxAttempts, BatchSize = batchSize },
            },
            clock ?? TimeProvider.System,
            NullLogger<EmailDispatcher>.Instance);

    private async Task<Guid> EnqueueAsync(EmailMessage message)
    {
        await using var conn = await OpenAsync();
        return await fx.Services.GetRequiredService<EmailOutbox>().EnqueueAsync(conn, null, message);
    }

    private async Task<(string Status, int Attempts, string? LastError, DateTimeOffset NextAttemptAt, string? Body)> RowAsync(Guid id)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QuerySingleAsync<(string, int, string?, DateTime, string?)>(
            "select status, attempts, last_error, next_attempt_at, body_protected from email_outbox where id = @id", new { id });
        return (row.Item1, row.Item2, row.Item3, new DateTimeOffset(DateTime.SpecifyKind(row.Item4, DateTimeKind.Utc)), row.Item5);
    }

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();

    private static string NewAddress() => $"e_{Guid.NewGuid():N}{Domain}";

    private static EmailMessage Message(string to, string html = "<p>Hola</p>", string? text = null) =>
        new("welcome", "es-MX", to, "Bienvenida a FX Intelligence", html, text);

    private sealed class FakeTransport : IMailTransport
    {
        private int _failures;

        public ConcurrentBag<OutboundEmail> Sent { get; } = [];

        public Exception? FailWith { get; init; }

        public int FailTimes { get; init; } = int.MaxValue;

        public TimeSpan Delay { get; init; }

        public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            if (FailWith is not null && Interlocked.Increment(ref _failures) <= FailTimes)
            {
                throw FailWith;
            }

            Sent.Add(email);
            return $"fake/{email.Id}";
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
