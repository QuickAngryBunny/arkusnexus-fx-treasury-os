using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FxOs.Platform;
using FxOs.Platform.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;

namespace FxOs.Api.Tests;

/// <summary>Email through SMTP (Email:Transport=smtp), for hosts without Google credentials such as Railway.</summary>
public sealed class SmtpMailTransportTests : IAsyncLifetime
{
    private readonly IContainer _mailpit = new ContainerBuilder()
        .WithImage("axllent/mailpit:v1.21.0")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v1/info")))
        .Build();

    public Task InitializeAsync() => _mailpit.StartAsync();

    public Task DisposeAsync() => _mailpit.DisposeAsync().AsTask();

    private static readonly OutboundEmail Email = new(
        Guid.Parse("6f1c2b8e-3a4d-4e5f-9a0b-1c2d3e4f5a6b"), "ana@example.test", "USD/MXN reached 19.00 · your alert",
        "<p>Hi Ana</p>", "Hi Ana");

    [Fact]
    public void The_message_has_the_sender_html_and_text_and_a_message_id_from_the_outbox_id()
    {
        var message = SmtpMailTransport.BuildMessage(Email, new SmtpSettings { FromAddress = "alerts@example.test", FromName = "FX Intelligence" });

        Assert.Equal("\"FX Intelligence\" <alerts@example.test>", message.From.ToString());
        Assert.Equal("ana@example.test", message.To.ToString());
        Assert.Equal("USD/MXN reached 19.00 · your alert", message.Subject);
        Assert.Equal("6f1c2b8e3a4d4e5f9a0b1c2d3e4f5a6b@fxos", message.MessageId);
        Assert.Equal("<p>Hi Ana</p>", message.HtmlBody);
        Assert.Equal("Hi Ana", message.TextBody);
    }

    [Fact]
    public async Task It_delivers_to_an_smtp_server()
    {
        var transport = new SmtpMailTransport(new EmailSettings
        {
            Transport = EmailTransport.Smtp,
            Smtp = new SmtpSettings
            {
                Host = _mailpit.Hostname, Port = _mailpit.GetMappedPublicPort(1025), Security = "none",
                FromAddress = "alerts@example.test",
            },
        });

        var reference = await transport.SendAsync(Email);

        Assert.Equal("smtp:6f1c2b8e3a4d4e5f9a0b1c2d3e4f5a6b@fxos", reference);
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}") };
        var inbox = await http.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        var stored = Assert.Single(inbox.GetProperty("messages").EnumerateArray());
        Assert.Equal("USD/MXN reached 19.00 · your alert", stored.GetProperty("Subject").GetString());
        Assert.Equal("ana@example.test", stored.GetProperty("To")[0].GetProperty("Address").GetString());
    }

    [Theory]
    [InlineData("smtp", typeof(SmtpMailTransport))]
    [InlineData("firebase", typeof(FirestoreMailTransport))]
    public void The_transport_setting_picks_the_sender(string transport, Type expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlPlane"] = "Host=localhost;Database=fx_control",
            ["TenantServers:default"] = "Host=localhost",
            ["Email:Transport"] = transport,
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFxPlatform(configuration, new HostingEnvironment { EnvironmentName = "Testing" });

        Assert.Equal(expected, services.Single(s => s.ServiceType == typeof(IMailTransport)).ImplementationType);
    }
}
