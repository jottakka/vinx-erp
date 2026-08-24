extern alias workerhost;

using System.Collections.Concurrent;
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
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Xunit;
using WorkerHostTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class MetaWhatsAppInboundTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";
    private const string ChannelPhoneNumberId = "111111111111111";
    private const string SenderWaId = "556198132901";

    private static async Task SeedAdmittedSenderAsync(
        FoundationApiFactory factory,
        string waId = SenderWaId
    )
    {
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{waId}",
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
        var logger = new CapturingLogger<InboundWhatsAppInteractionService>();

        using (var posted = await PostSignedAsync(client, ImagePayload("wamid.in.e2e", "556198132901", "1938", "isto é um recibo")))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, "wamid.in.e2e");
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(database, services, logger)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == "wamid.in.e2e");
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
        AssertReplyDecision(logger.Entries, LogLevel.Information, "reply_send_started", "provider_attempt");
        AssertReplyDecision(logger.Entries, LogLevel.Information, "reply_provider_accepted", "wamid_received");
        AssertReplyDecision(logger.Entries, LogLevel.Information, "reply_confirmed", "wamid_persisted");
    }

    [Fact]
    public async Task Text_before_image_is_sent_with_ocr_in_one_ai_turn()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);

        using (var text = await PostSignedAsync(
            client,
            TextPayload("wamid.in.text-before-image", SenderWaId, "check this purchase")
        ))
        {
            Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        }

        using (var image = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.image-after-text", SenderWaId, "media.after-text", null)
        ))
        {
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        }

        var imageId = await ReadInboundIdAsync(factory, "wamid.in.image-after-text");
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(database, services)
                .ProcessAsync(imageId, CancellationToken.None);
        }

        using var request = JsonDocument.Parse(Assert.Single(textAi.RequestBodies));
        var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(
            "User text:\ncheck this purchase\n\nOCR transcription:\nRECIBO 250 REAIS",
            messages[1].GetProperty("content").GetString()
        );
        Assert.Equal(1, meta.ReplyPosts);
        Assert.Equal(
            "completed",
            (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
                .Single(row => row.MessageId == "wamid.in.image-after-text")
                .Status
        );
    }

    [Fact]
    public async Task Image_without_text_processes_as_image_only_after_the_deadline()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        const string messageId = "wamid.in.image-only-delayed";

        using (var posted = await PostSignedAsync(
            client,
            ImagePayload(messageId, SenderWaId, "media.image-only", null)
        ))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        var queuedMessages = await ReadInboundQueueAsync(factory);
        var queued = Assert.Single(queuedMessages);
        Assert.True(queued.Scheduled);
        Assert.EndsWith("InboundWhatsAppImageReceived", queued.MessageType, StringComparison.Ordinal);
        Assert.Equal(inboundId, queued.InboundMessageId);
        Assert.StartsWith("postgresql://", queued.Destination, StringComparison.Ordinal);
        var createdAt = await ReadInboundTimestampAsync(factory, inboundId, "created_at");
        Assert.InRange((queued.ExecutionTime!.Value - createdAt).TotalSeconds, 29, 31);
        Assert.Equal((0, 0, 0, 0, 0), ExternalWork(meta, textAi, visionAi));

        await ExpireCorrelationDeadlineAsync(factory, inboundId);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(database, services)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        using var request = JsonDocument.Parse(Assert.Single(textAi.RequestBodies));
        var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(
            "OCR transcription:\nRECIBO 250 REAIS",
            messages[1].GetProperty("content").GetString()
        );
        Assert.Equal(1, meta.ReplyPosts);
    }

    [Fact]
    public async Task Text_without_image_expires_without_ai_or_meta_calls()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        const string messageId = "wamid.in.text-only-expiry";

        using (var posted = await PostSignedAsync(
            client,
            TextPayload(messageId, SenderWaId, "text without image")
        ))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        var queued = Assert.Single(await ReadInboundQueueAsync(factory));
        Assert.True(queued.Scheduled);
        Assert.EndsWith(
            "InboundWhatsAppTextCorrelationExpired",
            queued.MessageType,
            StringComparison.Ordinal
        );
        Assert.Equal(inboundId, queued.InboundMessageId);
        await ExpireCorrelationDeadlineAsync(factory, inboundId);

        var logger = new CapturingLogger<WorkerHostTypes.InboundWhatsAppTextCorrelationExpiredHandler>();
        await using (var database = CreateWorkerDatabase(factory))
        {
            var handler = new WorkerHostTypes.InboundWhatsAppTextCorrelationExpiredHandler(
                database,
                logger
            );
            await handler.Handle(
                new InboundWhatsAppTextCorrelationExpired(inboundId),
                CancellationToken.None
            );
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == messageId);
        Assert.Equal("failed", row.Status);
        Assert.Equal("correlation_timeout", row.FailureReason);
        Assert.Equal((0, 0, 0, 0, 0), ExternalWork(meta, textAi, visionAi));
        var decision = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, decision.Level);
        Assert.Equal("expired", decision.Properties["Result"]);
        Assert.Equal("deadline_elapsed", decision.Properties["Reason"]);
        Assert.DoesNotContain(inboundId.ToString("D"), decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signed_webhook_with_two_admitted_texts_publishes_both_expiry_envelopes()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        var logs = new CapturingLoggerProvider();
        factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var client = factory.CreateClient();
        const string firstMessageId = "wamid.in.text-batch.1";
        const string secondMessageId = "wamid.in.text-batch.2";
        var payload = $$"""
            {
              "entry": [{
                "changes": [{
                  "value": {
                    "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" },
                    "messages": [
                      {
                        "id": "{{firstMessageId}}",
                        "type": "text",
                        "from": "{{SenderWaId}}",
                        "text": { "body": "first batch text" }
                      },
                      {
                        "id": "{{secondMessageId}}",
                        "type": "text",
                        "from": "{{SenderWaId}}",
                        "text": { "body": "second batch text" }
                      }
                    ]
                  }
                }]
              }]
            }
            """;

        using (var posted = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var firstInboundId = await ReadInboundIdAsync(factory, firstMessageId);
        var secondInboundId = await ReadInboundIdAsync(factory, secondMessageId);
        var scheduled = (await ReadInboundQueueAsync(factory))
            .Where(envelope =>
                envelope.MessageType.EndsWith(
                    "InboundWhatsAppTextCorrelationExpired",
                    StringComparison.Ordinal
                )
            )
            .ToArray();
        Assert.Equal(2, scheduled.Length);
        Assert.All(scheduled, envelope => Assert.True(envelope.Scheduled));
        Assert.All(
            scheduled,
            envelope => Assert.StartsWith(
                "postgresql://",
                envelope.Destination,
                StringComparison.Ordinal
            )
        );
        Assert.Equal(
            new[] { firstInboundId, secondInboundId }.Order(),
            scheduled.Select(envelope => envelope.InboundMessageId).Order()
        );
        Assert.DoesNotContain(
            logs.Messages,
            message => message.Contains(
                "has already flushed its outgoing messages",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public async Task Text_correlation_expiry_dead_letter_redacts_the_inbound_id()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var client = worker.CreateClient();
        var interceptor = Assert.Single(
            worker.Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>()
        );
        var inboundId = Guid.NewGuid();
        var envelope = new Envelope(new InboundWhatsAppTextCorrelationExpired(inboundId));
        envelope.Headers["diagnostic"] = "sensitive";

        await interceptor.BeforeStoreAsync(
            envelope,
            new InvalidOperationException("test failure"),
            CancellationToken.None
        );

        Assert.Equal(
            Guid.Empty,
            Assert.IsType<InboundWhatsAppTextCorrelationExpired>(envelope.Message)
                .InboundMessageId
        );
        Assert.Empty(envelope.Headers);
        Assert.DoesNotContain(
            inboundId.ToString("D"),
            Encoding.UTF8.GetString(envelope.Data!),
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task Image_before_text_is_sent_with_ocr_in_one_ai_turn()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        await PostOkAsync(
            client,
            ImagePayload("wamid.in.image-before-text", SenderWaId, "media.before-text", null)
        );
        await PostOkAsync(
            client,
            TextPayload("wamid.in.text-after-image", SenderWaId, "review this purchase")
        );
        var imageId = await ReadInboundIdAsync(factory, "wamid.in.image-before-text");
        Assert.Equal(2, (await ReadInboundQueueAsync(factory)).Count);
        await ExpireCorrelationDeadlineAsync(factory, imageId);

        var (meta, textAi, _) = await ProcessImageAsync(factory, imageId);
        Assert.Equal(
            "User text:\nreview this purchase\n\nOCR transcription:\nRECIBO 250 REAIS",
            ReadUserTurn(textAi)
        );
        Assert.Equal(1, meta.ReplyPosts);
    }

    [Fact]
    public async Task Out_of_window_text_is_not_added_to_the_image_prompt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        await PostOkAsync(
            client,
            TextPayload("wamid.in.text-out-of-window", SenderWaId, "private old context")
        );
        var textId = await ReadInboundIdAsync(factory, "wamid.in.text-out-of-window");
        await ExpireCorrelationDeadlineAsync(factory, textId);
        await PostOkAsync(
            client,
            ImagePayload("wamid.in.image-out-of-window", SenderWaId, "media.out-of-window", null)
        );
        var imageId = await ReadInboundIdAsync(factory, "wamid.in.image-out-of-window");
        await ExpireCorrelationDeadlineAsync(factory, imageId);

        var (meta, textAi, _) = await ProcessImageAsync(factory, imageId);
        Assert.Equal("OCR transcription:\nRECIBO 250 REAIS", ReadUserTurn(textAi));
        Assert.DoesNotContain("private old context", textAi.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal(1, meta.ReplyPosts);
    }

    [Fact]
    public async Task Concurrent_senders_on_the_same_channel_never_exchange_text_context()
    {
        const string secondSender = "556198132902";
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        await SeedAdmittedSenderAsync(factory, secondSender);
        using var client = factory.CreateClient();
        await Task.WhenAll(
            PostOkAsync(
                client,
                TextPayload("wamid.in.text-sender-a", SenderWaId, "sender a private context")
            ),
            PostOkAsync(
                client,
                ImagePayload("wamid.in.image-sender-b", secondSender, "media.sender-b", null)
            )
        );
        var imageId = await ReadInboundIdAsync(factory, "wamid.in.image-sender-b");
        await ExpireCorrelationDeadlineAsync(factory, imageId);

        var (meta, textAi, _) = await ProcessImageAsync(factory, imageId);
        Assert.Equal("OCR transcription:\nRECIBO 250 REAIS", ReadUserTurn(textAi));
        Assert.DoesNotContain("sender a private context", textAi.RequestBodies[0], StringComparison.Ordinal);
        using var reply = JsonDocument.Parse(Assert.Single(meta.ReplyRequests).Body);
        Assert.Equal(secondSender, reply.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Text_and_image_replay_create_one_pair_and_one_internal_processing_result()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var textPayload = TextPayload("wamid.in.text-replay-pair", SenderWaId, "replay purchase");
        var imagePayload = ImagePayload(
            "wamid.in.image-replay-pair",
            SenderWaId,
            "media.replay-pair",
            null
        );
        await PostOkAsync(client, textPayload);
        await PostOkAsync(client, imagePayload);
        await PostOkAsync(client, textPayload);
        await PostOkAsync(client, imagePayload);
        var imageId = await ReadInboundIdAsync(factory, "wamid.in.image-replay-pair");
        var textId = await ReadInboundIdAsync(factory, "wamid.in.text-replay-pair");
        Assert.Equal(2, (await ReadInboundQueueAsync(factory)).Count);

        var (meta, textAi, visionAi) = await ProcessImageAsync(factory, imageId, processTwice: true);
        Assert.Equal(
            "User text:\nreplay purchase\n\nOCR transcription:\nRECIBO 250 REAIS",
            ReadUserTurn(textAi)
        );
        Assert.Single(visionAi.RequestBodies);
        Assert.Equal(1, meta.ReplyPosts);
        Assert.Equal(1, await CountPairAsync(factory, imageId, textId));
        Assert.Equal(2, (await NotificationIntentSeed.ReadInboundRowsAsync(factory)).Count);
    }

    [Fact]
    public async Task Whitespace_only_ai_reply_uses_the_fallback_before_the_reply_barrier()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, " \t\r\n ");
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        const string messageId = "wamid.in.reply-whitespace";

        using (var posted = await PostSignedAsync(client, ImagePayload(messageId, SenderWaId, "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(database, services)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == messageId);
        Assert.Equal("completed", row.Status);
        Assert.Equal("wamid.reply.test.1", row.ReplyMessageId);
        var reply = Assert.Single(meta.ReplyRequests);
        using var payload = JsonDocument.Parse(reply.Body);
        Assert.Equal(
            "Recebi sua imagem, mas não consegui gerar uma resposta agora. Tente novamente em alguns minutos.",
            payload.RootElement.GetProperty("text").GetProperty("body").GetString()
        );
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

        var inboundId = await ReadInboundIdAsync(factory, "wamid.in.once");
        await ExpireCorrelationDeadlineAsync(factory, inboundId);
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using (var database = CreateWorkerDatabase(factory))
        {
            var interaction = CreateInteractionService(database, services);
            await interaction.ProcessAsync(inboundId, CancellationToken.None);
            await interaction.ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == "wamid.in.once");
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
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        var firstLogger = new CapturingLogger<InboundWhatsAppInteractionService>();
        var redeliveryLogger = new CapturingLogger<InboundWhatsAppInteractionService>();
        const string messageId = "wamid.in.reply-process-loss";

        using (var posted = await PostSignedAsync(client, ImagePayload(messageId, SenderWaId, "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        using var services = CreateInteractionServices(meta, textAi, visionAi);

        await using (var firstDatabase = CreateWorkerDatabase(factory, completionFailure))
        {
            var interaction = CreateInteractionService(firstDatabase, services, firstLogger);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                interaction.ProcessAsync(inboundId, CancellationToken.None));
        }

        var externalWork = ExternalWork(meta, textAi, visionAi);

        await using (var redeliveryDatabase = CreateWorkerDatabase(factory, completionFailure))
        {
            var interaction = CreateInteractionService(redeliveryDatabase, services, redeliveryLogger);
            await interaction.ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory)).Single(row => row.MessageId == messageId);
        Assert.Equal(1, meta.ReplyPosts);
        Assert.Equal("reply_in_flight", row.Status);
        Assert.Null(row.ReplyMessageId);
        Assert.Null(row.FailureReason);
        Assert.Equal(externalWork, ExternalWork(meta, textAi, visionAi));
        AssertReplyDecision(firstLogger.Entries, LogLevel.Information, "reply_send_started", "provider_attempt");
        AssertReplyDecision(firstLogger.Entries, LogLevel.Information, "reply_provider_accepted", "wamid_received");
        AssertReplyDecision(redeliveryLogger.Entries, LogLevel.Information, "reply_redelivery_blocked", "reply_in_flight");
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
            { "missing_wamid_property", HttpStatusCode.OK, "{\"messages\":[{}]}", null, "reply_ambiguous" },
            { "non_string_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":123}]}", null, "reply_ambiguous" },
            { "empty_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":\"\"}]}", null, "reply_ambiguous" },
            { "whitespace_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":\"   \"}]}", null, "reply_ambiguous" },
            { "padded_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":\" wamid.reply.test.1 \"}]}", null, "reply_ambiguous" },
            { "oversized_wamid", HttpStatusCode.OK, "{\"messages\":[{\"id\":\"" + new string('w', 201) + "\"}]}", null, "reply_ambiguous" },
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
        var externalWork = ExternalWork(meta, textAi, visionAi);

        await using (var redeliveryDatabase = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(redeliveryDatabase, services)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        Assert.Equal(externalWork, ExternalWork(meta, textAi, visionAi));
    }

    [Fact]
    public async Task Barrier_race_emits_one_duplicate_blocked_decision_without_a_second_post()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        var firstLogger = new CapturingLogger<InboundWhatsAppInteractionService>();
        var secondLogger = new CapturingLogger<InboundWhatsAppInteractionService>();
        const string messageId = "wamid.in.reply-barrier-race";

        using (var posted = await PostSignedAsync(client, ImagePayload(messageId, SenderWaId, "1938", null)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var inboundId = await ReadInboundIdAsync(factory, messageId);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using var firstDatabase = CreateWorkerDatabase(factory);
        await using var secondDatabase = CreateWorkerDatabase(factory);
        await Task.WhenAll(
            CreateInteractionService(firstDatabase, services, firstLogger).ProcessAsync(inboundId, CancellationToken.None),
            CreateInteractionService(secondDatabase, services, secondLogger).ProcessAsync(inboundId, CancellationToken.None)
        );

        var entries = firstLogger.Entries.Concat(secondLogger.Entries).ToArray();
        Assert.Equal(1, meta.ReplyPosts);
        AssertReplyDecision(entries, LogLevel.Information, "reply_duplicate_blocked", "reply_in_flight");
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

        AssertReplyDecision(logger.Entries, LogLevel.Warning, "reply_ambiguous", "reply_ambiguous");
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

        var inboundId = await ReadInboundIdAsync(factory, "wamid.in.noai");
        await ExpireCorrelationDeadlineAsync(factory, inboundId);
        using var services = CreateInteractionServices(meta);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(database, services, configureAi: false)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == "wamid.in.noai");
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

        var inboundId = await ReadInboundIdAsync(factory, "wamid.in.404");
        await ExpireCorrelationDeadlineAsync(factory, inboundId);
        using var services = CreateInteractionServices(meta);
        await using (var database = CreateWorkerDatabase(factory))
        {
            await CreateInteractionService(database, services)
                .ProcessAsync(inboundId, CancellationToken.None);
        }

        var row = (await NotificationIntentSeed.ReadInboundRowsAsync(factory))
            .Single(row => row.MessageId == "wamid.in.404");
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
        MetaWhatsAppOptions? options = null,
        bool configureAi = true
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
            configureAi ? InboundAiTestProfiles.Create() : new InboundWhatsAppAiOptions(),
            logger ?? NullLogger<InboundWhatsAppInteractionService>.Instance
        );

    private static (int MediaMetadataGets, int MediaDownloads, int VisionRequests, int TextRequests, int ReplyPosts) ExternalWork(
        FakeInboundMetaHandler meta,
        FakeInboundAiHandler textAi,
        FakeInboundAiHandler visionAi
    ) => (meta.MediaMetadataGets, meta.MediaDownloads, visionAi.RequestBodies.Count, textAi.RequestBodies.Count, meta.ReplyPosts);

    private static void AssertReplyDecision(
        IEnumerable<CapturedLogEntry> entries,
        LogLevel level,
        string result,
        string reason
    )
    {
        var decision = Assert.Single(
            entries,
            entry => entry.Properties.GetValueOrDefault("Result")?.ToString() == result
        );
        Assert.Equal(level, decision.Level);
        Assert.Equal("whatsapp_inbound_interaction", decision.Properties["Operation"]);
        Assert.Equal("notifications", decision.Properties["Module"]);
        Assert.Equal(result, decision.Properties["Result"]);
        Assert.Equal(reason, decision.Properties["Reason"]);
        Assert.Equal(
            ["Module", "Operation", "Reason", "Result", "{OriginalFormat}"],
            decision.Properties.Keys.OrderBy(key => key, StringComparer.Ordinal)
        );
        Assert.Null(decision.Exception);
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

    private static string TextPayload(string messageId, string waId, string body) => $$"""
        {
          "entry": [{
            "changes": [{
              "value": {
                "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" },
                "messages": [{
                  "id": "{{messageId}}",
                  "type": "text",
                  "from": "{{waId}}",
                  "text": { "body": "{{body}}" }
                }]
              }
            }]
          }]
        }
        """;

    private sealed record InboundQueueEnvelope(
        string MessageType,
        Guid InboundMessageId,
        bool Scheduled,
        DateTime? ExecutionTime,
        string Destination
    );

    private static async Task<IReadOnlyList<InboundQueueEnvelope>> ReadInboundQueueAsync(
        FoundationApiFactory factory
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT body, false, NULL::timestamp with time zone
            FROM vinx_wolverine_queues.wolverine_queue_whatsapp_inbound
            WHERE body IS NOT NULL
            UNION ALL
            SELECT body, true, execution_time
            FROM vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_scheduled
            WHERE body IS NOT NULL
            UNION ALL
            SELECT body, false, NULL::timestamp with time zone
            FROM vinx_wolverine.wolverine_outgoing_envelopes
            WHERE body IS NOT NULL
            UNION ALL
            SELECT body, false, NULL::timestamp with time zone
            FROM vinx_wolverine.wolverine_incoming_envelopes
            WHERE body IS NOT NULL
            """,
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<InboundQueueEnvelope>();
        while (await reader.ReadAsync())
        {
            var envelope = EnvelopeSerializer.Deserialize(reader.GetFieldValue<byte[]>(0));
            using var payload = JsonDocument.Parse(envelope.Data!);
            DateTime? executionTime = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            if (executionTime is null && envelope.ScheduledTime.HasValue)
            {
                executionTime = envelope.ScheduledTime.Value.UtcDateTime;
            }
            result.Add(
                new InboundQueueEnvelope(
                    envelope.MessageType!,
                    payload.RootElement.GetProperty("inboundMessageId").GetGuid(),
                    reader.GetBoolean(1) || envelope.ScheduledTime.HasValue,
                    executionTime,
                    envelope.Destination?.ToString() ?? string.Empty
                )
            );
        }

        return result;
    }

    private static async Task<DateTime> ReadInboundTimestampAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        string column
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT {column} FROM notifications.inbound_whatsapp_messages WHERE id = @id",
            connection
        );
        command.Parameters.AddWithValue("id", inboundId);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExpireCorrelationDeadlineAsync(
        FoundationApiFactory factory,
        Guid inboundId
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE notifications.inbound_whatsapp_messages
            SET created_at = statement_timestamp() - interval '31 seconds',
                correlation_deadline = statement_timestamp() - interval '1 second'
            WHERE id = @id
            """,
            connection
        );
        command.Parameters.AddWithValue("id", inboundId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task PostOkAsync(HttpClient client, string payload)
    {
        using var response = await PostSignedAsync(client, payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(
        FakeInboundMetaHandler Meta,
        FakeInboundAiHandler TextAi,
        FakeInboundAiHandler VisionAi
    )> ProcessImageAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        bool processTwice = false
    )
    {
        var meta = new FakeInboundMetaHandler();
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, AiReplyText);
        var visionAi = new FakeInboundAiHandler(HttpStatusCode.OK, OcrStrictJson);
        using var services = CreateInteractionServices(meta, textAi, visionAi);
        await using var database = CreateWorkerDatabase(factory);
        var interaction = CreateInteractionService(database, services);
        await interaction.ProcessAsync(inboundId, CancellationToken.None);
        if (processTwice)
        {
            await interaction.ProcessAsync(inboundId, CancellationToken.None);
        }

        return (meta, textAi, visionAi);
    }

    private static string ReadUserTurn(FakeInboundAiHandler textAi)
    {
        using var request = JsonDocument.Parse(Assert.Single(textAi.RequestBodies));
        var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        return messages[1].GetProperty("content").GetString()!;
    }

    private static async Task<int> CountPairAsync(
        FoundationApiFactory factory,
        Guid imageId,
        Guid textId
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.inbound_whatsapp_messages "
                + "WHERE id = @image_id AND correlated_text_id = @text_id",
            connection
        );
        command.Parameters.AddWithValue("image_id", imageId);
        command.Parameters.AddWithValue("text_id", textId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Sink(_messages);

        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => messages.Enqueue(formatter(state, exception));
        }
    }
}
