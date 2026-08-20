using System.Net;
using System.Text;
using System.Text.Json;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class MetaWhatsAppInboundTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";

    private const string OcrStrictJson =
        "{\"text\":\"RECIBO 250 REAIS\",\"confidence\":0.9,\"language\":\"pt\"}";

    private const string AiReplyText = "Recebi seu recibo de 250 reais.";

    [Fact]
    public async Task Image_is_transcribed_by_ocr_and_answered_with_an_ai_reply()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);

        using (var posted = await PostSignedAsync(client, ImagePayload("wamid.in.e2e", "556198132901", "1938", "isto é um recibo")))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: textAi,
            inboundVisionAiHandler: visionAi
        );
        using var _ = worker.CreateClient();

        var row = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.e2e",
            "completed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("wamid.reply.test.1", row.ReplyMessageId);
        Assert.Null(row.FailureReason);
        Assert.Equal(1, meta.MediaMetadataGets);
        Assert.Equal(1, meta.MediaDownloads);
        Assert.Single(visionAi.RequestBodies);
        Assert.Single(textAi.RequestBodies);

        // OCR entra como dado: o turno de texto recebe legenda e transcrição,
        // nunca instrução.
        var textRequest = textAi.RequestBodies[0];
        Assert.Contains("isto é um recibo", textRequest, StringComparison.Ordinal);
        Assert.Contains("RECIBO 250 REAIS", textRequest, StringComparison.Ordinal);

        var reply = Assert.Single(meta.ReplyRequests);
        Assert.NotNull(reply.Authorization);
        using var payload = JsonDocument.Parse(reply.Body);
        var root = payload.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("556198132901", root.GetProperty("to").GetString());
        Assert.Equal("text", root.GetProperty("type").GetString());
        Assert.Equal(AiReplyText, root.GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Webhook_replay_processes_the_image_only_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var payload = ImagePayload("wamid.in.once", "556198132901", "1938", null);

        using (var first = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var replay = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText),
            inboundVisionAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson)
        );
        using var _ = worker.CreateClient();

        var row = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.once",
            "completed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("wamid.reply.test.1", row.ReplyMessageId);
        Assert.Equal(1, meta.ReplyPosts);
    }

    [Fact]
    public async Task Without_ai_profiles_the_image_fails_closed_without_external_calls()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();

        using (var posted = await PostSignedAsync(client, ImagePayload("wamid.in.noai", "556198132901", "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta
        );
        using var _ = worker.CreateClient();

        var row = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.noai",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("ai_unconfigured", row.FailureReason);
        Assert.Equal(0, meta.MediaMetadataGets);
        Assert.Equal(0, meta.MediaDownloads);
        Assert.Equal(0, meta.ReplyPosts);
    }

    [Fact]
    public async Task Rejected_media_fails_permanently_with_a_stable_reason()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler { FailMediaMetadata = true };

        using (var posted = await PostSignedAsync(client, ImagePayload("wamid.in.404", "556198132901", "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText),
            inboundVisionAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson)
        );
        using var _ = worker.CreateClient();

        var row = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.404",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("media_unavailable", row.FailureReason);
        Assert.Equal(0, meta.ReplyPosts);
    }

    [Fact]
    public async Task Transient_media_failure_retries_and_then_completes()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler { FailFirstDownloadOnce = true };

        using (var posted = await PostSignedAsync(client, ImagePayload("wamid.in.5xx", "556198132901", "1938", "recibo")))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText),
            inboundVisionAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson)
        );
        using var _ = worker.CreateClient();

        var row = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.5xx",
            "completed",
            TimeSpan.FromSeconds(45)
        );
        Assert.Equal("wamid.reply.test.1", row.ReplyMessageId);
        Assert.True(meta.MediaDownloads >= 2);
        Assert.Equal(1, meta.ReplyPosts);
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

    private static string ImagePayload(string messageId, string waId, string mediaId, string? caption)
    {
        var captionField = caption is null ? string.Empty : $", \"caption\": \"{caption}\"";
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
