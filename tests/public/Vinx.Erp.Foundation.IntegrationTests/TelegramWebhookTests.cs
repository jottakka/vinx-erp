using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Notifications.Providers.Telegram;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class TelegramWebhookTests
{
    private const string WebhookPath = "/foundation/notifications/telegram/webhook";
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const string WebhookSecret = "telegram-test-secret";

    [Fact]
    public void Verifier_requires_the_exact_configured_token_without_normalization()
    {
        Assert.True(TelegramWebhookVerifier.Verify(WebhookSecret, WebhookSecret));
        Assert.False(TelegramWebhookVerifier.Verify(null, WebhookSecret));
        Assert.False(TelegramWebhookVerifier.Verify($" {WebhookSecret}", WebhookSecret));
        Assert.False(
            TelegramWebhookVerifier.Verify(
                WebhookSecret.ToUpperInvariant(),
                WebhookSecret
            )
        );
    }

    [Fact]
    public async Task Private_text_with_the_configured_secret_is_forwarded_to_the_request_scoped_ingress()
    {
        InboundTelegramText? received = null;
        await using var application = await CreateApplicationAsync(
            (services, inbound, cancellationToken) =>
            {
                Assert.NotNull(services.GetService<TelegramOptions>());
                received = inbound;
                return Task.CompletedTask;
            }
        );
        using var client = application.GetTestClient();

        using var response = await PostAsync(client, PrivateTextPayload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"code\":\"accepted\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(new InboundTelegramText(101, 202, 303, "Olá"), received);
    }

    [Fact]
    public async Task Invalid_or_multiple_secret_headers_are_rejected_before_malformed_json_is_parsed()
    {
        var ingressCalls = 0;
        await using var application = await CreateApplicationAsync(
            (_, _, _) =>
            {
                ingressCalls++;
                return Task.CompletedTask;
            }
        );
        using var client = application.GetTestClient();

        using var wrongSecret = await PostAsync(client, "{not-json", "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        using var multipleSecrets = await PostAsync(
            client,
            PrivateTextPayload(),
            new[] { WebhookSecret, "second-secret" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, multipleSecrets.StatusCode);
        Assert.Equal(0, ingressCalls);
    }

    [Fact]
    public async Task Valid_secret_with_malformed_json_is_rejected()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        using var response = await PostAsync(client, "{not-json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_rejected_before_json_parsing_or_ingress()
    {
        var ingressCalls = 0;
        await using var application = await CreateApplicationAsync(
            (_, _, _) =>
            {
                ingressCalls++;
                return Task.CompletedTask;
            }
        );
        using var client = application.GetTestClient();

        using var response = await PostAsync(client, new string('x', 24 * 1024 + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, ingressCalls);
    }

    [Fact]
    public async Task Well_formed_but_unsupported_updates_are_accepted_without_ingress()
    {
        var ingressCalls = 0;
        await using var application = await CreateApplicationAsync(
            (_, _, _) =>
            {
                ingressCalls++;
                return Task.CompletedTask;
            }
        );
        using var client = application.GetTestClient();

        using var response = await PostAsync(
            client,
            """
            {"update_id":101,"message":{"message_id":303,"from":{"id":202,"is_bot":false},"chat":{"id":202,"type":"group"},"text":"Olá"}}
            """
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":\"accepted\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, ingressCalls);
    }

    [Fact]
    public async Task Unicode_whitespace_only_text_is_accepted_without_ingress()
    {
        var ingressCalls = 0;
        await using var application = await CreateApplicationAsync(
            (_, _, _) =>
            {
                ingressCalls++;
                return Task.CompletedTask;
            }
        );
        using var client = application.GetTestClient();

        using var response = await PostAsync(
            client,
            """
            {"update_id":101,"message":{"message_id":303,"from":{"id":202,"is_bot":false},"chat":{"id":202,"type":"private"},"text":"\u00A0"}}
            """
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":\"accepted\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, ingressCalls);
    }

    [Fact]
    public async Task Unconfigured_webhook_is_unavailable_before_header_or_body_processing()
    {
        await using var application = await CreateApplicationAsync(
            options: new TelegramOptions()
        );
        using var client = application.GetTestClient();

        using var response = await PostAsync(client, "{not-json", "wrong-secret");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Only_post_is_mapped_and_content_type_must_be_json_compatible()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        using var getResponse = await client.GetAsync(WebhookPath);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);

        using var formContent = new StringContent(PrivateTextPayload(), Encoding.UTF8, "text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = formContent,
            Headers = { { SecretHeader, WebhookSecret } },
        };
        using var contentTypeResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, contentTypeResponse.StatusCode);
    }

    private static async Task<WebApplication> CreateApplicationAsync(
        Func<IServiceProvider, InboundTelegramText, CancellationToken, Task>? ingress = null,
        TelegramOptions? options = null
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(options ?? new TelegramOptions
        {
            WebhookSecretToken = WebhookSecret,
        });

        var application = builder.Build();
        application.MapTelegramWebhookEndpoints(ingress ?? ((_, _, _) => Task.CompletedTask));
        await application.StartAsync();
        return application;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string payload,
        string secret = WebhookSecret
    ) => await PostAsync(client, payload, new[] { secret });

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string payload,
        IReadOnlyCollection<string> secrets
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        foreach (var secret in secrets)
        {
            request.Headers.Add(SecretHeader, secret);
        }

        return await client.SendAsync(request);
    }

    private static string PrivateTextPayload() =>
        """
        {"update_id":101,"message":{"message_id":303,"from":{"id":202,"is_bot":false},"chat":{"id":202,"type":"private"},"text":"Olá"}}
        """;
}
