using System.Net;
using System.Text;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class MetaWhatsAppInboundWebhookTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";

    [Fact]
    public async Task Signed_image_message_creates_a_pending_inbound_row()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.1", "556198132901", "1938", "isto é um recibo")
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("wamid.in.1", row.MessageId);
        Assert.Equal("556198132901", row.WaId);
        Assert.Equal("1938", row.MediaId);
        Assert.Equal("isto é um recibo", row.Caption);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.ReplyMessageId);
        Assert.Null(row.FailureReason);
    }

    [Fact]
    public async Task Replay_of_the_same_webhook_does_not_duplicate_the_inbound_row()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var payload = ImagePayload("wamid.in.2", "556198132901", "1939", null);

        using (var first = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var replay = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("wamid.in.2", row.MessageId);
    }

    [Fact]
    public async Task Non_image_messages_are_ignored_without_any_row()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        var textMessage =
            """
            {
              "object": "whatsapp_business_account",
              "entry": [
                {
                  "id": "123",
                  "changes": [
                    {
                      "value": {
                        "messages": [
                          {
                            "id": "wamid.in.text",
                            "type": "text",
                            "from": "556198132901",
                            "text": { "body": "olá" }
                          }
                        ]
                      },
                      "field": "messages"
                    }
                  ]
                }
              ]
            }
            """;
        using var response = await PostSignedAsync(client, textMessage);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
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

    private static string ImagePayload(
        string messageId,
        string waId,
        string mediaId,
        string? caption
    )
    {
        var captionField = caption is null
            ? string.Empty
            : $", \"caption\": \"{caption}\"";
        return $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "123",
              "changes": [
                {
                  "value": {
                    "messages": [
                      {
                        "id": "{{messageId}}",
                        "type": "image",
                        "from": "{{waId}}",
                        "image": { "id": "{{mediaId}}"{{captionField}} }
                      }
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
}
