extern alias workerhost;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Telegram;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Xunit;
using Xunit.Abstractions;
using WorkerHostTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Contrato da confirmação técnica da fila Telegram: <c>completed</c> é apenas
/// o recibo do worker e nunca uma confirmação para a pessoa remetente.
/// </summary>
[Collection("notification-worker")]
public sealed class TelegramInboundWorkerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Inbound_worker_uses_canonical_unconfigured_telegram_http_registration()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useCanonicalTelegramHttpRegistration: true
        );
        using var workerClient = worker.CreateClient();
        await using var scope = worker.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TelegramOptions>>().Value;
        var handler = scope.ServiceProvider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(TelegramClient.HttpClientName);

        Assert.False(options.OutboundIsConfigured);
        Assert.Equal(string.Empty, options.BotToken);
        Assert.Equal("https://api.telegram.org", options.ApiBaseUrl);
        Assert.Null(FindHttpHandler<FailFastTelegramBotHandler>(handler));
        var sockets = Assert.IsType<SocketsHttpHandler>(FindHttpHandler<SocketsHttpHandler>(handler));
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
    }

    [Fact]
    public async Task Public_webhook_and_real_worker_complete_an_admitted_pending_row_as_queue_receipt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "930001",
            telegramContactValidated: true
        );
        using var apiClient = factory.CreateClient();

        using (var response = await PostPrivateAsync(
            apiClient,
            updateId: 9301,
            chatId: 930001,
            messageId: 93001,
            text: "public receipt-only text"
        ))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var pending = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("pending", pending.Status);
        Assert.Equal("admitted", pending.ResolutionState);
        Assert.Null(pending.CompletedAt);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useCanonicalTelegramHttpRegistration: true
        );
        using var workerClient = worker.CreateClient();
        using var health = await workerClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var completed = await WaitForInboundStatusAsync(factory, pending.Id, "completed");

        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("public receipt-only text", completed.TextBody);
    }

    [Fact]
    public async Task Real_worker_persists_an_invalid_envelope_as_a_redacted_telegram_dead_letter()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var apiClient = factory.CreateClient();
        using var apiHealth = await apiClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, apiHealth.StatusCode);
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useCanonicalTelegramHttpRegistration: true
        );
        using var workerClient = worker.CreateClient();
        using var workerHealth = await workerClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, workerHealth.StatusCode);
        var secretHeader = "telegram-invalid-secret-header";
        var options = new DeliveryOptions().WithHeader("diagnostic", secretHeader);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .PublishAsync(new InboundTelegramTextReceived(Guid.Empty), options);
        }

        var deadLetter = await WaitForTelegramDeadLetterAsync(factory);
        var envelope = EnvelopeSerializer.Deserialize(deadLetter.Body);
        Assert.Equal("telegram_inbound_invalid", deadLetter.ExceptionType);
        Assert.Equal(
            "Inbound Telegram receipt rejected the envelope.",
            deadLetter.ExceptionMessage
        );
        Assert.Empty(envelope.Headers);
        Assert.NotNull(envelope.Data);
        using var payload = JsonDocument.Parse(envelope.Data!);
        Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("inboundMessageId").GetGuid());
        Assert.DoesNotContain(
            secretHeader,
            Encoding.UTF8.GetString(deadLetter.Body),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(secretHeader, deadLetter.ExceptionMessage, StringComparison.Ordinal);
        Assert.Equal(1L, await CountTelegramDeadLettersAsync(factory));
    }

    [Fact]
    public async Task Racing_real_worker_envelopes_report_one_transition_and_replays_are_noops()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "930002",
            telegramContactValidated: true
        );
        using var apiClient = factory.CreateClient();
        using (var response = await PostPrivateAsync(
            apiClient,
            updateId: 9302,
            chatId: 930002,
            messageId: 93002,
            text: "racing receipt-only text"
        ))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var pending = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        await PublishTelegramEnvelopeAsync(factory, pending.Id);
        await PublishTelegramEnvelopeAsync(factory, pending.Id);
        await WaitForTelegramQueueCountAsync(factory, expectedCount: 3);
        var firstLog = Path.Combine(Path.GetTempPath(), $"telegram-worker-{Guid.NewGuid():N}.log");
        var secondLog = Path.Combine(Path.GetTempPath(), $"telegram-worker-{Guid.NewGuid():N}.log");

        IReadOnlyList<string> receiptLogs;
        try
        {
            await using (var firstWorker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: firstLog,
                useCanonicalTelegramHttpRegistration: true
            ))
            await using (var secondWorker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: secondLog,
                useCanonicalTelegramHttpRegistration: true
            ))
            {
                using var firstClient = firstWorker.CreateClient();
                using var secondClient = secondWorker.CreateClient();
                using var firstHealth = await firstClient.GetAsync("/health");
                using var secondHealth = await secondClient.GetAsync("/health");
                Assert.Equal(HttpStatusCode.OK, firstHealth.StatusCode);
                Assert.Equal(HttpStatusCode.OK, secondHealth.StatusCode);
                await WaitForInboundStatusAsync(factory, pending.Id, "completed");
                receiptLogs = await WaitForReceiptDecisionsAsync(
                    [firstLog, secondLog],
                    queueReceiptCount: 1,
                    replayCount: 2
                );
            }

            Assert.Single(receiptLogs, line =>
                line.Contains("completed queue_receipt", StringComparison.Ordinal)
            );
            Assert.Equal(
                2,
                receiptLogs.Count(line => line.Contains("noop replay", StringComparison.Ordinal))
            );
            Assert.All(receiptLogs, line =>
            {
                Assert.DoesNotContain(pending.Id.ToString("D"), line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("racing receipt-only text", line, StringComparison.Ordinal);
                Assert.DoesNotContain("930002", line, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FoundationApiFactory.TelegramWebhookSecret,
                    line,
                    StringComparison.Ordinal
                );
            });
        }
        finally
        {
            File.Delete(firstLog);
            File.Delete(secondLog);
        }
    }

    [Fact]
    public async Task Real_worker_keeps_a_non_admitted_webhook_row_as_pending_evidence()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var apiClient = factory.CreateClient();
        using (var response = await PostPrivateAsync(
            apiClient,
            updateId: 9303,
            chatId: 930003,
            messageId: 93003,
            text: "closed evidence text"
        ))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var closed = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("unbound", closed.ResolutionState);
        Assert.Equal("pending", closed.Status);
        Assert.Null(closed.CompletedAt);
        var closedRows = new List<(Guid Id, string ResolutionState)>
        {
            (closed.Id, "unbound"),
        };
        foreach (var (resolutionState, discriminator) in new[]
        {
            ("unverified", 9401L),
            ("inactive", 9402L),
            ("ambiguous", 9403L),
        })
        {
            var inboundId = Guid.NewGuid();
            Assert.Equal(
                "created",
                await AdmitClosedAsync(
                    factory.Fixture.RuntimeConnectionString,
                    inboundId,
                    resolutionState,
                    discriminator
                )
            );
            closedRows.Add((inboundId, resolutionState));
        }

        foreach (var (inboundId, _) in closedRows)
        {
            await PublishTelegramEnvelopeAsync(factory, inboundId);
        }

        await WaitForTelegramQueueCountAsync(factory, expectedCount: 4);

        var workerLog = Path.Combine(Path.GetTempPath(), $"telegram-worker-{Guid.NewGuid():N}.log");
        try
        {
            await using (var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: workerLog,
                useCanonicalTelegramHttpRegistration: true
            ))
            {
                using var workerClient = worker.CreateClient();
                using var health = await workerClient.GetAsync("/health");
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                await WaitForReceiptDecisionsAsync(
                    [workerLog],
                    notAdmittedCount: 4
                );
            }

            var retained = await TelegramNotificationSeed.ReadInboundMessagesAsync(factory);
            Assert.Equal(4, retained.Count);
            foreach (var (inboundId, resolutionState) in closedRows)
            {
                var row = Assert.Single(retained, candidate => candidate.Id == inboundId);
                Assert.Equal(resolutionState, row.ResolutionState);
                Assert.Equal("pending", row.Status);
                Assert.Null(row.CompletedAt);
            }

            Assert.Equal(
                "closed evidence text",
                Assert.Single(retained, candidate => candidate.Id == closed.Id).TextBody
            );
            Assert.Equal(0L, await CountTelegramDeadLettersAsync(factory));
        }
        finally
        {
            File.Delete(workerLog);
        }
    }

    [Fact]
    public async Task Unexpected_database_failure_is_dead_lettered_once_without_sensitive_content()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var apiClient = factory.CreateClient();
        using var apiHealth = await apiClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, apiHealth.StatusCode);
        var originalFunction = await ReplaceCompletionFunctionWithUnexpectedFailureAsync(factory);
        var inboundId = Guid.NewGuid();
        const string failureSecret = "telegram-unexpected-secret";
        const string failureDetailSecret = "telegram-unexpected-detail-secret";
        const string headerSecret = "telegram-unexpected-header";
        var workerLog = Path.Combine(Path.GetTempPath(), $"telegram-worker-{Guid.NewGuid():N}.log");

        try
        {
            await using (var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: workerLog,
                useCanonicalTelegramHttpRegistration: true
            ))
            {
                using var workerClient = worker.CreateClient();
                using var workerHealth = await workerClient.GetAsync("/health");
                Assert.Equal(HttpStatusCode.OK, workerHealth.StatusCode);
                await PublishTelegramEnvelopeAsync(factory, inboundId, headerSecret);

                var deadLetter = await WaitForTelegramDeadLetterAsync(factory);
                Assert.Equal("telegram_inbound_unexpected", deadLetter.ExceptionType);
                Assert.Equal(
                    "Inbound Telegram receipt failed unexpectedly.",
                    deadLetter.ExceptionMessage
                );
                AssertTelegramDeadLetterIsRedacted(
                    deadLetter,
                    inboundId.ToString("D"),
                    failureSecret,
                    failureDetailSecret,
                    headerSecret
                );
                Assert.Equal(new AttemptSequenceState(1, true), await ReadAttemptCountAsync(factory));
                Assert.Equal(1L, await CountTelegramDeadLettersAsync(factory));
                AssertWorkerLogIsRedacted(
                    File.ReadAllText(workerLog),
                    inboundId,
                    headerSecret,
                    failureSecret,
                    failureDetailSecret,
                    "PostgresException",
                    "P0001"
                );
            }
        }
        finally
        {
            await RestoreCompletionFunctionAsync(factory, originalFunction);
            File.Delete(workerLog);
        }
    }

    [Fact]
    public async Task Transient_database_failure_retries_after_one_five_and_thirty_seconds_then_redacts_dlq()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var apiClient = factory.CreateClient();
        using var apiHealth = await apiClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, apiHealth.StatusCode);
        var originalFunction = await ReplaceCompletionFunctionWithTransientFailureAsync(factory);
        var inboundId = Guid.NewGuid();
        const string failureSecret = "telegram-transient-secret";
        const string failureDetailSecret = "telegram-transient-detail-secret";
        const string headerSecret = "telegram-transient-header";
        var workerLog = Path.Combine(Path.GetTempPath(), $"telegram-worker-{Guid.NewGuid():N}.log");
        using var observationCancellation = new CancellationTokenSource();

        try
        {
            await using (var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: workerLog,
                useCanonicalTelegramHttpRegistration: true
            ))
            {
                using var workerClient = worker.CreateClient();
                using var workerHealth = await workerClient.GetAsync("/health");
                Assert.Equal(HttpStatusCode.OK, workerHealth.StatusCode);
                var scheduledDelays = ObserveScheduledRetryDelaysAsync(
                    factory,
                    observationCancellation.Token
                );
                await PublishTelegramEnvelopeAsync(factory, inboundId, headerSecret);

                var deadLetter = await WaitForTelegramDeadLetterAsync(
                    factory,
                    TimeSpan.FromSeconds(130)
                );
                Assert.Equal("telegram_inbound_retry_exhausted", deadLetter.ExceptionType);
                Assert.Equal(
                    "Inbound Telegram receipt exhausted transient retries.",
                    deadLetter.ExceptionMessage
                );
                AssertTelegramDeadLetterIsRedacted(
                    deadLetter,
                    inboundId.ToString("D"),
                    failureSecret,
                    failureDetailSecret,
                    headerSecret
                );

                var observedScheduledDelays = await scheduledDelays;
                Assert.Equal(3, observedScheduledDelays.Count);
                Assert.InRange(
                    observedScheduledDelays[0],
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(3)
                );
                Assert.InRange(
                    observedScheduledDelays[1],
                    TimeSpan.FromSeconds(4.5),
                    TimeSpan.FromSeconds(7)
                );
                Assert.InRange(
                    observedScheduledDelays[2],
                    TimeSpan.FromSeconds(29),
                    TimeSpan.FromSeconds(33)
                );
                output.WriteLine(
                    "scheduled_delays_ms={0},{1},{2}",
                    observedScheduledDelays[0].TotalMilliseconds,
                    observedScheduledDelays[1].TotalMilliseconds,
                    observedScheduledDelays[2].TotalMilliseconds
                );

                var attempts = await ReadAttemptTimestampsAsync(factory);
                Assert.Equal(4, attempts.Count);
                output.WriteLine(
                    "retry_intervals_ms={0},{1},{2}",
                    (attempts[1] - attempts[0]).TotalMilliseconds,
                    (attempts[2] - attempts[1]).TotalMilliseconds,
                    (attempts[3] - attempts[2]).TotalMilliseconds
                );
                Assert.InRange(
                    attempts[1] - attempts[0],
                    TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromSeconds(20)
                );
                Assert.InRange(
                    attempts[2] - attempts[1],
                    TimeSpan.FromSeconds(4.5),
                    TimeSpan.FromSeconds(20)
                );
                Assert.InRange(
                    attempts[3] - attempts[2],
                    TimeSpan.FromSeconds(29),
                    TimeSpan.FromSeconds(60)
                );
                Assert.Equal(1L, await CountTelegramDeadLettersAsync(factory));
                AssertWorkerLogIsRedacted(
                    File.ReadAllText(workerLog),
                    inboundId,
                    headerSecret,
                    failureSecret,
                    failureDetailSecret,
                    "PostgresException",
                    "NpgsqlException",
                    "08006"
                );
            }
        }
        finally
        {
            observationCancellation.Cancel();
            await RestoreCompletionFunctionAsync(factory, originalFunction);
            File.Delete(workerLog);
        }
    }

    [Fact]
    public async Task Runtime_cannot_complete_a_pending_row_and_only_worker_has_function_execute()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "930004",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 930004);
        var inboundId = Guid.NewGuid();
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "runtime denial evidence"
            )
        );

        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CompleteAsync(factory.Fixture.RuntimeConnectionString, inboundId)
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var grants = new NpgsqlCommand(
            """
            SELECT
                has_function_privilege(
                    'vinx_worker',
                    'notifications.complete_inbound_telegram_text(uuid)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_runtime',
                    'notifications.complete_inbound_telegram_text(uuid)',
                    'EXECUTE')
            """,
            administrator
        );
        await using var reader = await grants.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(inboundId, row.Id);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task Public_handler_propagates_cancellation_without_recording_a_receipt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "930005",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 930005);
        var inboundId = Guid.NewGuid();
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "cancelled receipt evidence"
            )
        );
        await using var database = CreateWorkerDatabase(factory);
        var handler = new WorkerHostTypes.InboundTelegramTextReceivedHandler(
            database,
            new CapturingLogger<WorkerHostTypes.InboundTelegramTextReceivedHandler>()
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.Handle(new InboundTelegramTextReceived(inboundId), cancellation.Token)
        );

        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(inboundId, row.Id);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task Real_worker_consumes_a_missing_row_envelope_as_a_noop_without_dlq()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var apiClient = factory.CreateClient();
        using var apiHealth = await apiClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, apiHealth.StatusCode);
        var missingId = Guid.NewGuid();
        await PublishTelegramEnvelopeAsync(factory, missingId);
        await WaitForTelegramQueueCountAsync(factory, expectedCount: 1);

        var workerLog = Path.Combine(Path.GetTempPath(), $"telegram-worker-{Guid.NewGuid():N}.log");
        try
        {
            await using (var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: workerLog,
                useCanonicalTelegramHttpRegistration: true
            ))
            {
                using var workerClient = worker.CreateClient();
                using var workerHealth = await workerClient.GetAsync("/health");
                Assert.Equal(HttpStatusCode.OK, workerHealth.StatusCode);
                await WaitForReceiptDecisionsAsync([workerLog], notFoundCount: 1);
            }

            Assert.Empty(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
            Assert.Equal(0L, await CountTelegramDeadLettersAsync(factory));
            Assert.DoesNotContain(
                missingId.ToString("D"),
                File.ReadAllText(workerLog),
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            File.Delete(workerLog);
        }
    }

    [Fact]
    public async Task Admitted_pending_message_completes_once_as_a_queue_receipt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "910001",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 910001);
        var inboundId = Guid.NewGuid();

        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "receipt-only text"
            )
        );

        var first = await CompleteAsync(factory.Fixture.WorkerConnectionString, inboundId);
        var replay = await CompleteAsync(factory.Fixture.WorkerConnectionString, inboundId);

        Assert.Equal(new TelegramInboundReceipt("completed", true), first);
        Assert.Equal(new TelegramInboundReceipt("completed", false), replay);
        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(inboundId, row.Id);
        Assert.Equal("receipt-only text", row.TextBody);
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
    }

    [Fact]
    public async Task Closed_and_missing_messages_return_stable_noop_receipts()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var closedId = Guid.NewGuid();

        Assert.Equal(
            "created",
            await AdmitClosedAsync(factory.Fixture.RuntimeConnectionString, closedId)
        );

        var closed = await CompleteAsync(factory.Fixture.WorkerConnectionString, closedId);
        var missing = await CompleteAsync(factory.Fixture.WorkerConnectionString, Guid.NewGuid());

        Assert.Equal(new TelegramInboundReceipt("not_admitted", false), closed);
        Assert.Equal(new TelegramInboundReceipt("not_found", false), missing);
        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(closedId, row.Id);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task Handler_completes_an_admitted_message_inside_the_active_ef_transaction()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "910002",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 910002);
        var inboundId = Guid.NewGuid();
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "transaction receipt text"
            )
        );

        var logger = new CapturingLogger<WorkerHostTypes.InboundTelegramTextReceivedHandler>();
        await using (var database = CreateWorkerDatabase(factory))
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var handler = new WorkerHostTypes.InboundTelegramTextReceivedHandler(database, logger);
            await handler.Handle(
                new InboundTelegramTextReceived(inboundId),
                CancellationToken.None
            );
            await transaction.CommitAsync();
        }

        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
        var decision = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, decision.Level);
        Assert.Equal("telegram_inbound_receipt", decision.Properties["Operation"]);
        Assert.Equal("notifications", decision.Properties["Module"]);
        Assert.Equal("completed", decision.Properties["Result"]);
        Assert.Equal("queue_receipt", decision.Properties["Reason"]);
        Assert.DoesNotContain(inboundId.ToString("D"), decision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
    }

    [Fact]
    public async Task Handler_opens_a_closed_ef_connection_for_an_admitted_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "910003",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 910003);
        var inboundId = Guid.NewGuid();
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "closed connection receipt text"
            )
        );

        await using (var database = CreateWorkerDatabase(factory))
        {
            Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
            var handler = new WorkerHostTypes.InboundTelegramTextReceivedHandler(
                database,
                new CapturingLogger<WorkerHostTypes.InboundTelegramTextReceivedHandler>()
            );
            await handler.Handle(
                new InboundTelegramTextReceived(inboundId),
                CancellationToken.None
            );
        }

        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
    }

    [Fact]
    public async Task Registered_dead_letter_interceptor_redacts_a_telegram_envelope_without_changing_its_stable_exception()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useCanonicalTelegramHttpRegistration: true
        );
        using var client = worker.CreateClient();
        var originalId = Guid.NewGuid();
        var envelope = new Envelope(new InboundTelegramTextReceived(originalId))
        {
            Data = Encoding.UTF8.GetBytes($"{{\"inboundMessageId\":\"{originalId:D}\",\"payload\":\"secret\"}}"),
        };
        envelope.Headers["diagnostic"] = "secret-header";
        var originalException = new WorkerHostTypes.InboundTelegramDeadLetterException(
            WorkerHostTypes.InboundTelegramDeadLetterException.InvalidExceptionType,
            "Inbound Telegram receipt rejected the envelope."
        );
        var interceptor = worker
            .Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>()
            .Single();

        var persistedException = await interceptor.BeforeStoreAsync(
            envelope,
            originalException,
            CancellationToken.None
        );

        var telegramException = Assert.IsType<WorkerHostTypes.InboundTelegramDeadLetterException>(
            persistedException
        );
        Assert.Equal(
            WorkerHostTypes.InboundTelegramDeadLetterException.InvalidExceptionType,
            telegramException.ExceptionType
        );
        Assert.Equal("Inbound Telegram receipt rejected the envelope.", telegramException.Message);
        Assert.Empty(envelope.Headers);
        var data = Encoding.UTF8.GetString(envelope.Data!);
        Assert.DoesNotContain(originalId.ToString("D"), data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", data, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(data);
        Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("inboundMessageId").GetGuid());
    }

    [Fact]
    public async Task Registered_dead_letter_interceptor_replaces_untrusted_telegram_exception_content()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useCanonicalTelegramHttpRegistration: true
        );
        using var client = worker.CreateClient();
        var secret = "telegram-provider-id-secret";
        var envelope = new Envelope(new InboundTelegramTextReceived(Guid.NewGuid()));
        var interceptor = worker
            .Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>()
            .Single();

        var persistedException = await interceptor.BeforeStoreAsync(
            envelope,
            new WorkerHostTypes.InboundTelegramDeadLetterException("untrusted", secret),
            CancellationToken.None
        );

        var telegramException = Assert.IsType<WorkerHostTypes.InboundTelegramDeadLetterException>(
            persistedException
        );
        Assert.Equal(
            WorkerHostTypes.InboundTelegramDeadLetterException.UnexpectedExceptionType,
            telegramException.ExceptionType
        );
        Assert.Equal("Inbound Telegram receipt failed unexpectedly.", telegramException.Message);
        Assert.DoesNotContain(secret, telegramException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, telegramException.ToString(), StringComparison.Ordinal);
    }

    private static async Task<string> AdmitAsync(
        string connectionString,
        Guid inboundId,
        TelegramInboundActorResolution resolution,
        string textBody
    )
    {
        await using var runtime = new NpgsqlConnection(connectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_telegram_text(
                @inbound_id, @update_id, @chat_id, @message_id, @text_body,
                @resolution_state, @user_id, @company_id, @membership_id)
            """,
            runtime
        );
        command.Parameters.AddWithValue("inbound_id", NpgsqlDbType.Uuid, inboundId);
        command.Parameters.AddWithValue("update_id", NpgsqlDbType.Bigint, 91L);
        command.Parameters.AddWithValue("chat_id", NpgsqlDbType.Bigint, 910001L);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Bigint, 9101L);
        command.Parameters.AddWithValue("text_body", NpgsqlDbType.Text, textBody);
        command.Parameters.AddWithValue("resolution_state", NpgsqlDbType.Text, resolution.ResolutionState);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Uuid, resolution.UserId!.Value);
        command.Parameters.AddWithValue("company_id", NpgsqlDbType.Uuid, resolution.CompanyId!.Value);
        command.Parameters.AddWithValue("membership_id", NpgsqlDbType.Uuid, resolution.MembershipId!.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> AdmitClosedAsync(
        string connectionString,
        Guid inboundId,
        string resolutionState = "unbound",
        long discriminator = 92L
    )
    {
        await using var runtime = new NpgsqlConnection(connectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_telegram_text(
                @inbound_id, @update_id, @chat_id, @message_id, @text_body,
                @resolution_state, NULL, NULL, NULL)
            """,
            runtime
        );
        command.Parameters.AddWithValue("inbound_id", NpgsqlDbType.Uuid, inboundId);
        command.Parameters.AddWithValue("update_id", NpgsqlDbType.Bigint, discriminator);
        command.Parameters.AddWithValue("chat_id", NpgsqlDbType.Bigint, discriminator * 100L);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Bigint, discriminator * 10L);
        command.Parameters.AddWithValue("text_body", NpgsqlDbType.Text, $"{resolutionState} closed receipt text");
        command.Parameters.AddWithValue("resolution_state", NpgsqlDbType.Text, resolutionState);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<TelegramInboundReceipt> CompleteAsync(
        string connectionString,
        Guid inboundId
    )
    {
        await using var worker = new NpgsqlConnection(connectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, transitioned FROM notifications.complete_inbound_telegram_text(@inbound_id)",
            worker
        );
        command.Parameters.AddWithValue("inbound_id", NpgsqlDbType.Uuid, inboundId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new TelegramInboundReceipt(reader.GetString(0), reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static FoundationDbContext CreateWorkerDatabase(FoundationApiFactory factory) =>
        new(
            new DbContextOptionsBuilder<FoundationDbContext>()
                .UseNpgsql(factory.Fixture.WorkerConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options
        );

    private static Task<HttpResponseMessage> PostPrivateAsync(
        HttpClient client,
        long updateId,
        long chatId,
        long messageId,
        string text
    )
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/notifications/telegram/webhook"
        )
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    update_id = updateId,
                    message = new
                    {
                        message_id = messageId,
                        from = new { id = chatId, is_bot = false },
                        chat = new { id = chatId, type = "private" },
                        text,
                    },
                }),
                Encoding.UTF8,
                "application/json"
            )
        };
        request.Headers.Add(
            "X-Telegram-Bot-Api-Secret-Token",
            FoundationApiFactory.TelegramWebhookSecret
        );
        return client.SendAsync(request);
    }

    private static async Task<TelegramInboundMessageRow> WaitForInboundStatusAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        string expectedStatus
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        TelegramInboundMessageRow? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = (await TelegramNotificationSeed.ReadInboundMessagesAsync(factory))
                .SingleOrDefault(row => row.Id == inboundId);
            if (last?.Status == expectedStatus)
            {
                return last;
            }

            await Task.Delay(100);
        }

        Assert.Fail(
            $"Inbound Telegram row did not reach {expectedStatus}; last status was {last?.Status ?? "missing"}."
        );
        throw new UnreachableException();
    }

    private static async Task<TelegramDeadLetterRow> WaitForTelegramDeadLetterAsync(
        FoundationApiFactory factory,
        TimeSpan? timeoutDuration = null
    )
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeoutDuration ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT body, exception_type, exception_message FROM vinx_wolverine_worker.wolverine_dead_letters ORDER BY id DESC LIMIT 1",
                administrator
            );
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new TelegramDeadLetterRow(
                    reader.GetFieldValue<byte[]>(0),
                    reader.GetString(1),
                    reader.GetString(2)
                );
            }

            await Task.Delay(100);
        }

        Assert.Fail("Worker did not persist a Telegram dead letter.");
        throw new UnreachableException();
    }

    private static async Task PublishTelegramEnvelopeAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        string? secretHeader = null
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        if (secretHeader is null)
        {
            await bus.PublishAsync(new InboundTelegramTextReceived(inboundId));
            return;
        }

        await bus.PublishAsync(
            new InboundTelegramTextReceived(inboundId),
            new DeliveryOptions().WithHeader("diagnostic", secretHeader)
        );
    }

    private static async Task WaitForTelegramQueueCountAsync(
        FoundationApiFactory factory,
        long expectedCount
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        long lastCount = -1;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM vinx_wolverine_queues.wolverine_queue_telegram_inbound",
                administrator
            );
            lastCount = (long)(await command.ExecuteScalarAsync())!;
            if (lastCount == expectedCount)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(
            $"Telegram inbound queue did not reach {expectedCount} rows; last count was {lastCount}."
        );
    }

    private static async Task<long> CountTelegramDeadLettersAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM vinx_wolverine_worker.wolverine_dead_letters",
            administrator
        );
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<string>> WaitForReceiptDecisionsAsync(
        IReadOnlyList<string> logPaths,
        int queueReceiptCount = 0,
        int replayCount = 0,
        int notAdmittedCount = 0,
        int notFoundCount = 0
    )
    {
        var expectedTotal = queueReceiptCount + replayCount + notAdmittedCount + notFoundCount;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        string[] receiptLogs = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            receiptLogs = logPaths
                .Where(File.Exists)
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Contains("telegram_inbound_receipt", StringComparison.Ordinal))
                .ToArray();
            if (
                receiptLogs.Length == expectedTotal
                && receiptLogs.Count(line => line.Contains("completed queue_receipt", StringComparison.Ordinal)) == queueReceiptCount
                && receiptLogs.Count(line => line.Contains("noop replay", StringComparison.Ordinal)) == replayCount
                && receiptLogs.Count(line => line.Contains("noop not_admitted", StringComparison.Ordinal)) == notAdmittedCount
                && receiptLogs.Count(line => line.Contains("noop not_found", StringComparison.Ordinal)) == notFoundCount
            )
            {
                return receiptLogs;
            }

            await Task.Delay(100);
        }

        Assert.Fail(
            $"Worker receipt decisions did not settle at queue_receipt={queueReceiptCount}, replay={replayCount}, not_admitted={notAdmittedCount}, not_found={notFoundCount}; observed: {string.Join(" | ", receiptLogs)}"
        );
        throw new UnreachableException();
    }

    private static async Task<string> ReplaceCompletionFunctionWithUnexpectedFailureAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var readDefinition = new NpgsqlCommand(
            "SELECT pg_get_functiondef('notifications.complete_inbound_telegram_text(uuid)'::regprocedure)",
            administrator
        );
        var originalFunction = (string)(await readDefinition.ExecuteScalarAsync())!;
        await using var replace = new NpgsqlCommand(
            """
            CREATE SEQUENCE notifications.telegram_worker_attempt_counter_test;
            GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                notifications.telegram_worker_attempt_counter_test
                TO vinx_notifications_owner;

            CREATE OR REPLACE FUNCTION notifications.complete_inbound_telegram_text(
                p_inbound_id uuid)
            RETURNS TABLE (status text, transitioned boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                PERFORM nextval('notifications.telegram_worker_attempt_counter_test');
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'telegram-unexpected-secret',
                    DETAIL = 'telegram-unexpected-detail-secret';
            END;
            $function$;
            """,
            administrator
        );
        await replace.ExecuteNonQueryAsync();
        return originalFunction;
    }

    private static async Task<AttemptSequenceState> ReadAttemptCountAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT last_value, is_called FROM notifications.telegram_worker_attempt_counter_test",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new AttemptSequenceState(reader.GetInt64(0), reader.GetBoolean(1));
    }

    private static async Task<string> ReplaceCompletionFunctionWithTransientFailureAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var readDefinition = new NpgsqlCommand(
            "SELECT pg_get_functiondef('notifications.complete_inbound_telegram_text(uuid)'::regprocedure)",
            administrator
        );
        var originalFunction = (string)(await readDefinition.ExecuteScalarAsync())!;
        await using var replace = new NpgsqlCommand(
            """
            CREATE SEQUENCE notifications.telegram_worker_attempt_counter_test;
            CREATE SEQUENCE notifications.telegram_worker_attempt_1_test MINVALUE 0 START WITH 0;
            CREATE SEQUENCE notifications.telegram_worker_attempt_2_test MINVALUE 0 START WITH 0;
            CREATE SEQUENCE notifications.telegram_worker_attempt_3_test MINVALUE 0 START WITH 0;
            CREATE SEQUENCE notifications.telegram_worker_attempt_4_test MINVALUE 0 START WITH 0;
            GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                notifications.telegram_worker_attempt_counter_test,
                notifications.telegram_worker_attempt_1_test,
                notifications.telegram_worker_attempt_2_test,
                notifications.telegram_worker_attempt_3_test,
                notifications.telegram_worker_attempt_4_test
                TO vinx_notifications_owner;

            CREATE OR REPLACE FUNCTION notifications.complete_inbound_telegram_text(
                p_inbound_id uuid)
            RETURNS TABLE (status text, transitioned boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_attempt bigint;
                v_epoch_milliseconds bigint;
            BEGIN
                v_attempt := nextval('notifications.telegram_worker_attempt_counter_test');
                v_epoch_milliseconds := floor(
                    extract(epoch FROM clock_timestamp()) * 1000
                )::bigint;
                CASE v_attempt
                    WHEN 1 THEN
                        PERFORM setval(
                            'notifications.telegram_worker_attempt_1_test',
                            v_epoch_milliseconds,
                            true);
                    WHEN 2 THEN
                        PERFORM setval(
                            'notifications.telegram_worker_attempt_2_test',
                            v_epoch_milliseconds,
                            true);
                    WHEN 3 THEN
                        PERFORM setval(
                            'notifications.telegram_worker_attempt_3_test',
                            v_epoch_milliseconds,
                            true);
                    WHEN 4 THEN
                        PERFORM setval(
                            'notifications.telegram_worker_attempt_4_test',
                            v_epoch_milliseconds,
                            true);
                    ELSE
                        RAISE EXCEPTION 'unexpected extra Telegram receipt attempt';
                END CASE;

                RAISE EXCEPTION USING
                    ERRCODE = '08006',
                    MESSAGE = 'telegram-transient-secret',
                    DETAIL = 'telegram-transient-detail-secret';
            END;
            $function$;
            """,
            administrator
        );
        await replace.ExecuteNonQueryAsync();
        return originalFunction;
    }

    private static async Task<IReadOnlyList<DateTimeOffset>> ReadAttemptTimestampsAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT last_value FROM notifications.telegram_worker_attempt_1_test),
                (SELECT last_value FROM notifications.telegram_worker_attempt_2_test),
                (SELECT last_value FROM notifications.telegram_worker_attempt_3_test),
                (SELECT last_value FROM notifications.telegram_worker_attempt_4_test)
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, 4)
            .Select(index => DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<TimeSpan>> ObserveScheduledRetryDelaysAsync(
        FoundationApiFactory factory,
        CancellationToken cancellationToken
    )
    {
        var delays = new List<TimeSpan>(3);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(75);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = await TryReadScheduledRetryAsync(factory, attempt);
                if (observation is not null)
                {
                    delays.Add(observation.ExecutionTime - observation.AttemptedAt);
                    break;
                }

                await Task.Delay(25, cancellationToken);
            }

            if (delays.Count != attempt)
            {
                Assert.Fail($"Worker did not persist scheduled Telegram retry {attempt}.");
            }
        }

        return delays;
    }

    private static async Task<ScheduledRetryObservation?> TryReadScheduledRetryAsync(
        FoundationApiFactory factory,
        int attempt
    )
    {
        var sequenceName = attempt switch
        {
            1 => "telegram_worker_attempt_1_test",
            2 => "telegram_worker_attempt_2_test",
            3 => "telegram_worker_attempt_3_test",
            _ => throw new ArgumentOutOfRangeException(nameof(attempt)),
        };
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT scheduled.execution_time, attempt.last_value, attempt.is_called
            FROM vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled AS scheduled
            CROSS JOIN notifications.{sequenceName} AS attempt
            WHERE scheduled.body IS NOT NULL
              AND attempt.is_called
            ORDER BY scheduled.execution_time DESC
            LIMIT 1
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ScheduledRetryObservation(
            new DateTimeOffset(reader.GetDateTime(0)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))
        );
    }

    private static async Task RestoreCompletionFunctionAsync(
        FoundationApiFactory factory,
        string originalFunction
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var restore = new NpgsqlCommand(
            $"""
            {originalFunction};
            DROP SEQUENCE IF EXISTS
                notifications.telegram_worker_attempt_counter_test,
                notifications.telegram_worker_attempt_1_test,
                notifications.telegram_worker_attempt_2_test,
                notifications.telegram_worker_attempt_3_test,
                notifications.telegram_worker_attempt_4_test;
            """,
            administrator
        );
        await restore.ExecuteNonQueryAsync();
    }

    private static void AssertTelegramDeadLetterIsRedacted(
        TelegramDeadLetterRow deadLetter,
        params string[] sensitiveValues
    )
    {
        var bodyText = Encoding.UTF8.GetString(deadLetter.Body);
        var envelope = EnvelopeSerializer.Deserialize(deadLetter.Body);
        Assert.Empty(envelope.Headers);
        Assert.NotNull(envelope.Data);
        using var payload = JsonDocument.Parse(envelope.Data!);
        Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("inboundMessageId").GetGuid());
        foreach (var sensitiveValue in sensitiveValues)
        {
            Assert.DoesNotContain(sensitiveValue, bodyText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                sensitiveValue,
                deadLetter.ExceptionMessage,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private static void AssertWorkerLogIsRedacted(
        string log,
        Guid inboundId,
        params string[] sensitiveValues
    )
    {
        Assert.DoesNotContain(inboundId.ToString("D"), log, StringComparison.OrdinalIgnoreCase);
        foreach (var sensitiveValue in sensitiveValues)
        {
            Assert.DoesNotContain(sensitiveValue, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static THandler? FindHttpHandler<THandler>(HttpMessageHandler handler)
        where THandler : HttpMessageHandler
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (current is THandler expected)
            {
                return expected;
            }
        }

        return null;
    }

    private sealed record TelegramInboundReceipt(string Status, bool Transitioned);
    private sealed record AttemptSequenceState(long LastValue, bool IsCalled);
    private sealed record ScheduledRetryObservation(
        DateTimeOffset ExecutionTime,
        DateTimeOffset AttemptedAt
    );
    private sealed record TelegramDeadLetterRow(
        byte[] Body,
        string ExceptionType,
        string ExceptionMessage
    );
}
