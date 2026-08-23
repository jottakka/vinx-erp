using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications.Providers.Meta;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class MetaWhatsAppInboundTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";
    private const string ChannelPhoneNumberId = "111111111111111";
    private const string SenderWaId = "556198132901";

    private static async Task SeedAdmittedSenderAsync(FoundationApiFactory factory)
    {
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
    }

    private const string OcrStrictJson =
        "{\"text\":\"RECIBO 250 REAIS\",\"confidence\":0.9,\"language\":\"pt\"}";

    private const string AiReplyText = "Recebi seu recibo de 250 reais.";

    [Fact]
    public async Task Image_is_transcribed_by_ocr_and_answered_with_an_ai_reply()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
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
        await SeedAdmittedSenderAsync(factory);
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
    public async Task Accepted_reply_then_process_loss_before_complete_is_not_sent_again_on_redelivery()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var completionFailure = new OneShotTransactionCommitFailureInterceptor();
        var meta = new FakeInboundMetaHandler { ReplyAccepted = completionFailure.Arm };
        const string messageId = "wamid.in.reply-process-loss";

        using (var posted = await PostSignedAsync(client, ImagePayload(messageId, SenderWaId, "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        using var services = CreateInteractionServices(meta);

        await using (var firstDatabase = CreateWorkerDatabase(factory, completionFailure))
        {
            var interaction = CreateInteractionService(firstDatabase, services);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                interaction.ProcessAsync(inboundId, CancellationToken.None));
        }

        await using (var redeliveryDatabase = CreateWorkerDatabase(factory, completionFailure))
        {
            var interaction = CreateInteractionService(redeliveryDatabase, services);
            await interaction.ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory)).Single(row => row.MessageId == messageId);
        Assert.Equal(1, meta.ReplyPosts);
        Assert.Equal("reply_in_flight", row.Status);
        Assert.Null(row.ReplyMessageId);
        Assert.Null(row.FailureReason);
    }

    public static TheoryData<string, HttpStatusCode, string, string?, string> PostBarrierOutcomes =>
        new()
        {
            { "definite_4xx", HttpStatusCode.BadRequest, "{}", null, "reply_rejected" },
            { "http_408", HttpStatusCode.RequestTimeout, "{}", null, "reply_ambiguous" },
            { "http_429", HttpStatusCode.TooManyRequests, "{}", null, "reply_ambiguous" },
            { "http_5xx", HttpStatusCode.InternalServerError, "{}", null, "reply_ambiguous" },
            { "transport", HttpStatusCode.OK, "{}", "transport", "reply_ambiguous" },
            { "timeout", HttpStatusCode.OK, "{}", "timeout", "reply_ambiguous" },
            { "malformed", HttpStatusCode.OK, "not-json", null, "reply_ambiguous" },
            { "missing_wamid", HttpStatusCode.OK, "{\"messages\":[]}", null, "reply_ambiguous" },
            { "empty_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":\"\"}]}", null, "reply_ambiguous" },
            { "whitespace_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":\"   \"}]}", null, "reply_ambiguous" },
        };

    [Theory]
    [MemberData(nameof(PostBarrierOutcomes))]
    public async Task Post_barrier_outcomes_are_terminal_and_redelivery_does_no_external_work(
        string scenario,
        HttpStatusCode statusCode,
        string responseBody,
        string? exceptionKind,
        string expectedFailureReason
    )
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler
        {
            ReplyStatusCode = statusCode,
            ReplyResponseBody = responseBody,
            ReplyException = exceptionKind switch
            {
                "transport" => new HttpRequestException("test transport failure"),
                "timeout" => new OperationCanceledException("test provider timeout"),
                _ => null,
            },
        };
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        const string messagePrefix = "wamid.in.reply-outcome.";
        var messageId = messagePrefix + scenario;

        using (var posted = await PostSignedAsync(client, ImagePayload(messageId, SenderWaId, "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var firstDatabase = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(firstDatabase, services)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        var firstRow = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == messageId);
        Assert.Equal("failed", firstRow.Status);
        Assert.Equal(expectedFailureReason, firstRow.FailureReason);
        Assert.Equal(1, meta.ReplyPosts);
        var externalWork = (meta.MediaMetadataGets, meta.MediaDownloads, textAi.RequestBodies.Count, visionAi.RequestBodies.Count, meta.ReplyPosts);

        await using (var redeliveryDatabase = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(redeliveryDatabase, services)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        Assert.Equal(externalWork, (meta.MediaMetadataGets, meta.MediaDownloads, textAi.RequestBodies.Count, visionAi.RequestBodies.Count, meta.ReplyPosts));
    }

    [Fact]
    public async Task Ambiguous_transport_logs_one_safe_application_decision_without_provider_exception()
    {
        const string sensitivePhone = "+5511999999999";
        const string sensitiveToken = "token-adversarial-123";
        const string sensitivePayload = "payload-adversarial-456";
        const string sensitiveWamid = "wamid-adversarial-789";
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler
        {
            ReplyException = new HttpRequestException($"{sensitivePhone} {sensitiveToken} {sensitivePayload} {sensitiveWamid}"),
        };
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        var logger = new CapturingLogger<InboundWhatsAppInteractionService>();
        const string messageId = "wamid.in.reply-safe-log";

        using (var posted = await PostSignedAsync(client, ImagePayload(messageId, SenderWaId, "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(
                database,
                services,
                logger,
                new MetaWhatsAppOptions
                {
                    PhoneNumberId = ChannelPhoneNumberId,
                    AccessToken = sensitiveToken,
                    WebAppBaseUrl = "https://app.example.test",
                }
            ).ProcessAsync(inboundId, CancellationToken.None);
        }

        var decision = Assert.Single(
            logger.Entries,
            entry => entry.Properties.GetValueOrDefault("Result")?.ToString() == "reply_ambiguous"
        );
        Assert.Equal(LogLevel.Warning, decision.Level);
        Assert.Equal("whatsapp_inbound_interaction", decision.Properties["Operation"]);
        Assert.Equal("notifications", decision.Properties["Module"]);
        Assert.Equal("reply_ambiguous", decision.Properties["Result"]);
        Assert.Equal("reply_ambiguous", decision.Properties["Reason"]);
        Assert.Null(decision.Exception);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        var observed = string.Join(
            "\n",
            logger.Entries.Select(entry => entry.Message + " " + string.Join(" ", entry.Properties.Values))
        );
        Assert.DoesNotContain(sensitivePhone, observed, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveToken, observed, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePayload, observed, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveWamid, observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_ai_profiles_the_image_fails_closed_without_external_calls()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
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
        await SeedAdmittedSenderAsync(factory);
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
        await SeedAdmittedSenderAsync(factory);
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

    private static async Task<Guid> ReadInboundIdAsync(FoundationApiFactory factory, string messageId)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM notifications.inbound_whatsapp_messages WHERE message_id = @message_id",
            administrator
        );
        command.Parameters.AddWithValue("message_id", messageId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static FoundationDbContext CreateWorkerDatabase(FoundationApiFactory factory) =>
        new(
            new DbContextOptionsBuilder<FoundationDbContext>()
                .UseNpgsql(factory.Fixture.WorkerConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options
        );

    private static FoundationDbContext CreateWorkerDatabase(
        FoundationApiFactory factory,
        OneShotTransactionCommitFailureInterceptor completionFailure
    ) =>
        new(
            new DbContextOptionsBuilder<FoundationDbContext>()
                .UseNpgsql(factory.Fixture.WorkerConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(completionFailure)
                .Options
        );

    private static ServiceProvider CreateInteractionServices(
        FakeInboundMetaHandler meta,
        FakeInboundAiHandler? textAi = null,
        FakeInboundAiHandler? visionAi = null
    )
    {
        textAi ??= new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        visionAi ??= new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        var services = new ServiceCollection();
        services
            .AddHttpClient(MetaWhatsAppClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => meta);
        services
            .AddHttpClient(InboundWhatsAppInteractionService.TextHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => textAi);
        services
            .AddHttpClient(InboundWhatsAppInteractionService.VisionHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => visionAi);
        return services.BuildServiceProvider();
    }

    private static InboundWhatsAppInteractionService CreateInteractionService(
        FoundationDbContext database,
        ServiceProvider services,
        ILogger<InboundWhatsAppInteractionService>? logger = null,
        MetaWhatsAppOptions? options = null
    ) =>
        new(
            database,
            services.GetRequiredService<IHttpClientFactory>(),
            options ?? new MetaWhatsAppOptions
            {
                PhoneNumberId = ChannelPhoneNumberId,
                AccessToken = "test-meta-access-token",
                WebAppBaseUrl = "https://app.example.test",
            },
            InboundAiTestProfiles.Create(),
            logger ?? NullLogger<InboundWhatsAppInteractionService>.Instance
        );

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
                    "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" },
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
