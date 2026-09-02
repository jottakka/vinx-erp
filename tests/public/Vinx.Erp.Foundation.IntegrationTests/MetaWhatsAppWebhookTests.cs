using System.Net;
using System.Text;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class MetaWhatsAppWebhookTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";
    private const string VerifyToken = "test-meta-verify-token";

    [Fact]
    public async Task Challenge_echoes_only_with_the_configured_verify_token()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var accepted = await client.GetAsync(
            $"{WebhookPath}?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=1234567890"
        );
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("1234567890", await accepted.Content.ReadAsStringAsync());

        using var denied = await client.GetAsync(
            $"{WebhookPath}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1234567890"
        );
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Anonymous_webhook_requests_hit_the_fixed_window_limit_per_ip()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        var limited = false;
        for (var attempt = 0; attempt < 130 && !limited; attempt++)
        {
            using var response = await client.GetAsync(
                $"{WebhookPath}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1234567890"
            );
            limited = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(limited);
    }

    [Fact]
    public async Task Unsigned_or_tampered_post_is_rejected_before_any_side_effect()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes(StatusPayload("wamid.a", "delivered"));

        using var missing = await client.PostAsync(
            WebhookPath,
            new ByteArrayContent(body)
        );
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var tampered = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, WebhookPath)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(StatusPayload("wamid.b", "delivered"))),
                Headers = { { "X-Hub-Signature-256", MetaWhatsAppWebhookVerifier.Compute(body, AppSecret) } },
            }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);

        Assert.Empty(await NotificationIntentSeed.ReadProviderEventsAsync(factory));
    }

    [Fact]
    public async Task Signed_statuses_are_recorded_once_and_replays_do_not_duplicate()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var payload = StatusPayload("wamid.dedupe", "delivered");

        using (var first = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var replay = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        var events = await NotificationIntentSeed.ReadProviderEventsAsync(factory);
        var record = Assert.Single(events);
        Assert.Equal("wamid.dedupe", record.MessageId);
        Assert.Equal("delivered", record.EventType);
    }

    [Fact]
    public async Task Unknown_or_out_of_order_events_are_ignored_without_error()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var unknownStatus = await PostSignedAsync(
            client,
            StatusPayload("wamid.unknown", "deleted")
        );
        Assert.Equal(HttpStatusCode.OK, unknownStatus.StatusCode);

        using var noStatuses = await PostSignedAsync(client, """{"object":"whatsapp_business_account","entry":[]}""");
        Assert.Equal(HttpStatusCode.OK, noStatuses.StatusCode);

        Assert.Empty(await NotificationIntentSeed.ReadProviderEventsAsync(factory));
    }

    [Fact]
    public async Task Oversized_body_is_rejected_without_reading_past_the_limit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var padding = new string('x', 26 * 1024);
        var oversized = Encoding.UTF8.GetBytes($$"""{"padding":"{{padding}}"}""");

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, WebhookPath)
            {
                Content = new ByteArrayContent(oversized),
                Headers = { { "X-Hub-Signature-256", MetaWhatsAppWebhookVerifier.Compute(oversized, AppSecret) } },
            }
        );
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            response.StatusCode
        );
    }

    private static async Task<HttpResponseMessage> PostSignedAsync(
        HttpClient client,
        string payload
    )
    {
        var body = Encoding.UTF8.GetBytes(payload);
        return await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, WebhookPath)
            {
                Content = new ByteArrayContent(body),
                Headers = { { "X-Hub-Signature-256", MetaWhatsAppWebhookVerifier.Compute(body, AppSecret) } },
            }
        );
    }

    private static string StatusPayload(string messageId, string status) =>
        $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "123",
              "changes": [
                {
                  "value": {
                    "statuses": [
                      { "id": "{{messageId}}", "status": "{{status}}", "timestamp": "1770000000" }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;
}
