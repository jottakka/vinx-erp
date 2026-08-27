extern alias workerhost;

using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Paguai;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Xunit;
using WorkerHostTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftCreatedTests
{
    private const string TemplateKey = "paguai.draft-created";

    [Fact]
    public async Task Created_draft_publishes_one_opaque_event_and_replay_keeps_one_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 1, approveTemplate: true);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, requestId)
        );
        using var replay = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, requestId)
        );

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var draftId = await ReadDraftIdAsync(created);
        Assert.Equal(draftId, await ReadDraftIdAsync(replay));

        var envelope = Assert.Single(await WaitForPaguaiQueueAsync(factory, expectedCount: 1));
        Assert.Equal(scenario.CompanyId, envelope.CompanyId);
        Assert.Equal(draftId, envelope.DraftId);
        Assert.Equal(factory.ActorUserId, envelope.RequesterUserId);
        Assert.Equal(
            ["companyId", "draftId", "requesterUserId"],
            envelope.PayloadPropertyNames.Order()
        );
        Assert.Empty(envelope.Headers);
        Assert.True(Guid.TryParse(envelope.CorrelationId, out var correlationId));
        Assert.NotEqual(Guid.Empty, correlationId);
        AssertOpaque(
            envelope,
            "Confidential office supplies",
            "Internal replacement details",
            "Sensitive Beneficiary",
            "1250.00",
            requestId.ToString("D")
        );
    }

    [Fact]
    public async Task Outbox_persistence_failure_rolls_back_draft_receipt_audit_and_envelope_then_retry_recovers()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 1, approveTemplate: true);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        await SetOutboxInsertAsync(factory, granted: false);

        using var unavailable = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, requestId)
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        await AssertCreateSideEffectsAsync(factory, scenario.CompanyId, requestId, expectedCount: 0);
        Assert.Empty(await ReadPaguaiQueueAsync(factory));

        await SetOutboxInsertAsync(factory, granted: true);
        using var recovered = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, requestId)
        );

        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        await AssertCreateSideEffectsAsync(factory, scenario.CompanyId, requestId, expectedCount: 1);
        Assert.Single(await WaitForPaguaiQueueAsync(factory, expectedCount: 1));
    }

    [Fact]
    public async Task Worker_resolves_current_company_approvers_excludes_requester_and_deduplicates_redelivery()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 2, approveTemplate: true);
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, Guid.NewGuid())
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draftId = await ReadDraftIdAsync(created);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(
                new PaguaiDraftCreated(scenario.CompanyId, draftId, factory.ActorUserId),
                new DeliveryOptions { CorrelationId = factory.CorrelationId.ToString("D") }
            );
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var workerClient = worker.CreateClient();
        var intents = await WaitForIntentsAsync(factory, draftId, scenario.ApproverUserIds.Count);

        Assert.Equal(
            scenario.ApproverUserIds.Order(),
            intents.Select(intent => intent.RecipientUserId).Order()
        );
        Assert.DoesNotContain(intents, intent => intent.RecipientUserId == factory.ActorUserId);
        Assert.DoesNotContain(intents, intent => intent.RecipientUserId == scenario.ForeignApproverId);
        Assert.All(intents, intent =>
        {
            Assert.Equal(scenario.CompanyId, intent.CompanyId);
            Assert.Equal("in_app", intent.Channel);
            Assert.Equal(TemplateKey, intent.TemplateKey);
            Assert.Equal($"paguai.{draftId:N}.draft-created", intent.EventKey);
            Assert.Equal($"/paguai/requests/{draftId:D}", intent.DeepLinkSelector);
        });

        await WaitForPaguaiWorkerQueueToDrainAsync(factory);
        Assert.Equal(
            scenario.ApproverUserIds.Count,
            (await ReadIntentsAsync(factory, draftId)).Count
        );
    }

    [Fact]
    public async Task Missing_template_is_best_effort_warning_without_reverting_the_draft()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 1, approveTemplate: false);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, requestId)
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draftId = await ReadDraftIdAsync(created);
        var logPath = Path.GetTempFileName();
        try
        {
            await using var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString,
                debugLogPath: logPath
            );
            using var workerClient = worker.CreateClient();
            await WaitForLogAsync(logPath, "template_unavailable");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(
                    new PaguaiDraftCreated(Guid.Empty, Guid.NewGuid(), factory.ActorUserId),
                    new DeliveryOptions { CorrelationId = factory.CorrelationId.ToString("D") }
                );
            }
            await WaitForLogAsync(logPath, "invalid_event");

            await AssertCreateSideEffectsAsync(
                factory,
                scenario.CompanyId,
                requestId,
                expectedCount: 1
            );
            Assert.Empty(await ReadIntentsAsync(factory, draftId));
            var logs = await File.ReadAllTextAsync(logPath);
            Assert.Contains("Warning", logs, StringComparison.Ordinal);
            Assert.DoesNotContain("Confidential office supplies", logs, StringComparison.Ordinal);
            Assert.DoesNotContain("Internal replacement details", logs, StringComparison.Ordinal);
            Assert.DoesNotContain("Sensitive Beneficiary", logs, StringComparison.Ordinal);
            Assert.DoesNotContain("1250.00", logs, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task Worker_uses_only_the_narrow_paguai_draft_notification_function_with_current_approvers_and_worker_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 1, approveTemplate: true);
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, Guid.NewGuid())
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draftId = await ReadDraftIdAsync(created);
        var correlationId = Guid.NewGuid();

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        await using (var contract = new NpgsqlCommand(
            """
            SELECT
                pg_get_userbyid(procedure.proowner),
                procedure.prosecdef,
                procedure.proconfig,
                has_function_privilege(
                    'vinx_worker',
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'public',
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_runtime',
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_migrator',
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_worker',
                    'notifications.enqueue_notification_intent(uuid,uuid,text,text,text,text)',
                    'EXECUTE'),
                NOT has_table_privilege('vinx_worker', 'notifications.notification_intents', 'SELECT'),
                NOT has_table_privilege('vinx_worker', 'notifications.notification_intents', 'INSERT'),
                NOT has_table_privilege('vinx_worker', 'paguai.requests', 'SELECT')
            FROM pg_proc AS procedure
            WHERE procedure.oid =
                'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)'::regprocedure;
            """,
            administrator
        ))
        {
            await using var reader = await contract.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("vinx_notifications_owner", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(
                ["search_path=pg_catalog, notifications, paguai, foundation, audit, pg_temp"],
                reader.GetFieldValue<string[]>(2)
            );
            for (var index = 3; index < reader.FieldCount; index++)
            {
                Assert.True(reader.GetBoolean(index));
            }
        }

        await using var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString);
        await worker.OpenAsync();
        Assert.Equal(
            "created",
            await EnqueuePaguaiDraftCreatedAsync(
                worker,
                scenario.CompanyId,
                draftId,
                factory.ActorUserId,
                correlationId
            )
        );
        Assert.Equal(
            "replay",
            await EnqueuePaguaiDraftCreatedAsync(
                worker,
                scenario.CompanyId,
                draftId,
                factory.ActorUserId,
                correlationId
            )
        );

        var mismatch = await Assert.ThrowsAsync<PostgresException>(() =>
            EnqueuePaguaiDraftCreatedAsync(
                worker,
                Guid.NewGuid(),
                draftId,
                factory.ActorUserId,
                correlationId
            )
        );
        Assert.Equal("22023", mismatch.SqlState);
        var requesterMismatch = await Assert.ThrowsAsync<PostgresException>(() =>
            EnqueuePaguaiDraftCreatedAsync(
                worker,
                scenario.CompanyId,
                draftId,
                Guid.NewGuid(),
                correlationId
            )
        );
        Assert.Equal("22023", requesterMismatch.SqlState);

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        await using (var revokeApprover = new NpgsqlCommand(
            "UPDATE foundation.role_assignments SET deleted_at = clock_timestamp() WHERE company_id = @company_id AND role_id <> (SELECT id FROM foundation.roles WHERE company_id = @company_id AND name = 'requester')",
            administrator
        ))
        {
            revokeApprover.Parameters.AddWithValue("company_id", scenario.CompanyId);
            await revokeApprover.ExecuteNonQueryAsync();
        }

        using var noApproverDraft = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, Guid.NewGuid())
        );
        Assert.Equal(HttpStatusCode.Created, noApproverDraft.StatusCode);
        Assert.Equal(
            "no_current_approvers",
            await EnqueuePaguaiDraftCreatedAsync(
                worker,
                scenario.CompanyId,
                await ReadDraftIdAsync(noApproverDraft),
                factory.ActorUserId,
                correlationId
            )
        );

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        await using (var audit = new NpgsqlCommand(
            """
            SELECT source, action, requested_company_id, actor_user_id, correlation_id
            FROM audit.audit_events
            WHERE action = 'notification.intent.enqueue'
              AND requested_company_id = @company_id
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            administrator
        ))
        {
            audit.Parameters.AddWithValue("company_id", scenario.CompanyId);
            await using var reader = await audit.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("worker", reader.GetString(0));
            Assert.Equal("notification.intent.enqueue", reader.GetString(1));
            Assert.Equal(scenario.CompanyId, reader.GetGuid(2));
            Assert.Equal(factory.ActorUserId, reader.GetGuid(3));
            Assert.Equal(correlationId, reader.GetGuid(4));
        }
    }

    [Fact]
    public async Task Paguai_dead_letter_is_typed_and_redacts_all_opaque_identifiers_and_headers()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var workerClient = worker.CreateClient();
        var companyId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var secret = "financial-payload-secret";
        var envelope = new Envelope(new PaguaiDraftCreated(companyId, draftId, requesterUserId));
        envelope.Headers["sensitive"] = secret;
        var interceptor = worker
            .Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>()
            .Single();

        var exception = await interceptor.BeforeStoreAsync(
            envelope,
            new InvalidOperationException(secret),
            CancellationToken.None
        );

        Assert.Equal("WorkerPaguaiDraftCreatedDeadLetterException", exception!.GetType().Name);
        var typed = Assert.IsAssignableFrom<IDeadLetterExceptionInfo>(exception);
        Assert.Equal("paguai_draft_created_unexpected", typed.ExceptionType);
        Assert.Equal("Paguai draft notification failed unexpectedly.", exception.Message);
        Assert.Empty(envelope.Headers);
        var redacted = Assert.IsType<PaguaiDraftCreated>(envelope.Message);
        Assert.Equal(Guid.Empty, redacted.CompanyId);
        Assert.Equal(Guid.Empty, redacted.DraftId);
        Assert.Equal(Guid.Empty, redacted.RequesterUserId);
        var payload = Encoding.UTF8.GetString(envelope.Data!);
        Assert.DoesNotContain(companyId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(draftId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(requesterUserId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_database_failure_retries_then_recovers_without_a_paguai_dead_letter()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 1, approveTemplate: true);
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, Guid.NewGuid())
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draftId = await ReadDraftIdAsync(created);
        await SetPaguaiIntentInsertTransientFailureAsync(factory, enabled: true);
        try
        {
            await using var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString
            );
            using var workerClient = worker.CreateClient();
            await WaitForPaguaiScheduledRetryAsync(factory);
            await SetPaguaiIntentInsertTransientFailureAsync(factory, enabled: false);

            var intents = await WaitForIntentsAsync(factory, draftId, expectedCount: 1);
            Assert.Single(intents);
            await WaitForPaguaiWorkerQueueToDrainAsync(factory);
            Assert.Equal(0, await CountPaguaiDeadLettersAsync(factory));
        }
        finally
        {
            await SetPaguaiIntentInsertTransientFailureAsync(factory, enabled: false);
        }
    }

    [Fact]
    public async Task Exhausted_transient_database_failure_is_redacted_to_the_typed_paguai_dead_letter()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, approverCount: 1, approveTemplate: true);
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewDraft(scenario.CompanyId, Guid.NewGuid())
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await SetPaguaiIntentInsertTransientFailureAsync(factory, enabled: true);
        try
        {
            await using var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString
            );
            using var workerClient = worker.CreateClient();
            await WaitForPaguaiScheduledRetryAsync(factory);
            await AdvancePaguaiRetryToTerminalAttemptAsync(factory);
            var deadLetter = await WaitForPaguaiDeadLetterAsync(factory);

            Assert.Equal("paguai_draft_created_retry_exhausted", deadLetter.ExceptionType);
            Assert.Equal(
                "Paguai draft notification exhausted transient retries.",
                deadLetter.ExceptionMessage
            );
            var envelope = EnvelopeSerializer.Deserialize(deadLetter.Body);
            Assert.Empty(envelope.Headers);
            using var payload = JsonDocument.Parse(envelope.Data!);
            Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("companyId").GetGuid());
            Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("draftId").GetGuid());
            Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("requesterUserId").GetGuid());
        }
        finally
        {
            await SetPaguaiIntentInsertTransientFailureAsync(factory, enabled: false);
        }
    }

    [Fact]
    public async Task Requested_cancellation_propagates_without_reclassification()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var handler = new WorkerHostTypes.PaguaiDraftCreatedHandler(
            scope.ServiceProvider.GetRequiredService<FoundationDbContext>(),
            NullLogger<WorkerHostTypes.PaguaiDraftCreatedHandler>.Instance
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.Handle(
                new PaguaiDraftCreated(Guid.NewGuid(), Guid.NewGuid(), factory.ActorUserId),
                new Envelope { CorrelationId = factory.CorrelationId.ToString("D") },
                cancellation.Token
            )
        );
    }

    private static object NewDraft(Guid companyId, Guid requestId) => new
    {
        companyId,
        requestId,
        title = "Confidential office supplies",
        description = "Internal replacement details",
        amount = "1250.00",
        currency = "BRL",
        dueDate = new DateOnly(2026, 9, 1),
        beneficiaryDisplayName = "Sensitive Beneficiary",
        proofRequirement = "required",
    };

    private static async Task<Guid> ReadDraftIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<PaguaiEnvelope>> WaitForPaguaiQueueAsync(
        FoundationApiFactory factory,
        int expectedCount
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        IReadOnlyList<PaguaiEnvelope> envelopes = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            envelopes = await ReadPaguaiQueueAsync(factory);
            if (envelopes.Count == expectedCount)
            {
                return envelopes;
            }

            await Task.Delay(100);
        }

        return envelopes;
    }

    private static async Task<IReadOnlyList<PaguaiEnvelope>> ReadPaguaiQueueAsync(
        FoundationApiFactory factory
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var exists = new NpgsqlCommand(
            "SELECT to_regclass('vinx_wolverine_queues.wolverine_queue_paguai_events')::text",
            connection
        );
        if (await exists.ExecuteScalarAsync() is not string)
        {
            return [];
        }

        await using var command = new NpgsqlCommand(
            "SELECT body, row_to_json(queue)::text FROM vinx_wolverine_queues.wolverine_queue_paguai_events AS queue WHERE body IS NOT NULL",
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<PaguaiEnvelope>();
        while (await reader.ReadAsync())
        {
            var body = reader.GetFieldValue<byte[]>(0);
            var envelope = EnvelopeSerializer.Deserialize(body);
            using var payload = JsonDocument.Parse(envelope.Data!);
            result.Add(
                new PaguaiEnvelope(
                    payload.RootElement.GetProperty("companyId").GetGuid(),
                    payload.RootElement.GetProperty("draftId").GetGuid(),
                    payload.RootElement.GetProperty("requesterUserId").GetGuid(),
                    payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray(),
                    envelope.CorrelationId ?? string.Empty,
                    envelope.Headers.ToDictionary(header => header.Key, header => header.Value ?? string.Empty),
                    body,
                    reader.GetString(1)
                )
            );
        }

        return result;
    }

    private static async Task<IReadOnlyList<IntentRow>> WaitForIntentsAsync(
        FoundationApiFactory factory,
        Guid draftId,
        int expectedCount
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        IReadOnlyList<IntentRow> rows = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            rows = await ReadIntentsAsync(factory, draftId);
            if (rows.Count == expectedCount)
            {
                return rows;
            }

            await Task.Delay(200);
        }

        return rows;
    }

    private static async Task<IReadOnlyList<IntentRow>> ReadIntentsAsync(
        FoundationApiFactory factory,
        Guid draftId
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT company_id, recipient_user_id, channel, event_key, template_key, deep_link_selector
            FROM notifications.notification_intents
            WHERE event_key = @event_key
            ORDER BY recipient_user_id
            """,
            connection
        );
        command.Parameters.AddWithValue("event_key", $"paguai.{draftId:N}.draft-created");
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<IntentRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(
                new IntentRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)
                )
            );
        }

        return rows;
    }

    private static async Task<string> EnqueuePaguaiDraftCreatedAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid draftId,
        Guid requesterUserId,
        Guid correlationId
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM notifications.enqueue_paguai_draft_created_notification(@company_id, @draft_id, @requester_user_id, @correlation_id)",
            connection
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("draft_id", draftId);
        command.Parameters.AddWithValue("requester_user_id", requesterUserId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForPaguaiWorkerQueueToDrainAsync(FoundationApiFactory factory)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT
                    (SELECT count(*) FROM vinx_wolverine_queues.wolverine_queue_paguai_events WHERE body IS NOT NULL),
                    (SELECT count(*) FROM vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled WHERE body IS NOT NULL)
                """,
                administrator
            );
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            if (reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Paguai worker envelopes did not both reach a terminal state.");
    }

    private static async Task WaitForPaguaiScheduledRetryAsync(FoundationApiFactory factory)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled WHERE body IS NOT NULL",
                administrator
            );
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Paguai worker did not schedule a transient database retry.");
    }

    private static async Task<long> CountPaguaiDeadLettersAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM vinx_wolverine_worker.wolverine_dead_letters WHERE message_type LIKE '%PaguaiDraftCreated%'",
            administrator
        );
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AdvancePaguaiRetryToTerminalAttemptAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var transaction = await administrator.BeginTransactionAsync();
        await using var read = new NpgsqlCommand(
            "SELECT id, body FROM vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled WHERE body IS NOT NULL FOR UPDATE",
            administrator,
            transaction
        );
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var envelopeId = reader.GetGuid(0);
        var envelope = EnvelopeSerializer.Deserialize(reader.GetFieldValue<byte[]>(1));
        await reader.CloseAsync();
        envelope.Attempts = 3;
        await using var update = new NpgsqlCommand(
            "UPDATE vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled SET body = @body, execution_time = now() WHERE id = @id",
            administrator,
            transaction
        );
        update.Parameters.AddWithValue("body", EnvelopeSerializer.Serialize(envelope));
        update.Parameters.AddWithValue("id", envelopeId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async Task<PaguaiDeadLetter> WaitForPaguaiDeadLetterAsync(
        FoundationApiFactory factory
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
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
                return new PaguaiDeadLetter(
                    reader.GetFieldValue<byte[]>(0),
                    reader.GetString(1),
                    reader.GetString(2)
                );
            }

            await Task.Delay(100);
        }

        Assert.Fail("Paguai worker did not persist an exhausted transient dead letter.");
        throw new UnreachableException();
    }

    private static async Task SetPaguaiIntentInsertTransientFailureAsync(
        FoundationApiFactory factory,
        bool enabled
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            enabled
                ? """
                  GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
                  SET ROLE vinx_notifications_owner;
                  CREATE OR REPLACE FUNCTION notifications.test_paguai_intent_transient_failure()
                  RETURNS trigger LANGUAGE plpgsql AS $function$
                  BEGIN
                      RAISE EXCEPTION 'Paguai transient database failure' USING ERRCODE = '08006';
                  END;
                  $function$;
                  DROP TRIGGER IF EXISTS test_paguai_intent_transient_failure ON notifications.notification_intents;
                  CREATE TRIGGER test_paguai_intent_transient_failure
                      BEFORE INSERT ON notifications.notification_intents
                      FOR EACH ROW EXECUTE FUNCTION notifications.test_paguai_intent_transient_failure();
                  RESET ROLE;
                  REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
                  """
                : """
                  GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
                  SET ROLE vinx_notifications_owner;
                  DROP TRIGGER IF EXISTS test_paguai_intent_transient_failure ON notifications.notification_intents;
                  DROP FUNCTION IF EXISTS notifications.test_paguai_intent_transient_failure();
                  RESET ROLE;
                  REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
                  """,
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForLogAsync(string path, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await File.ReadAllTextAsync(path)).Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Contains(expected, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    private static async Task SetOutboxInsertAsync(
        FoundationApiFactory factory,
        bool granted
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"{(granted ? "GRANT" : "REVOKE")} INSERT ON vinx_wolverine.wolverine_outgoing_envelopes {(granted ? "TO" : "FROM")} vinx_runtime",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCreateSideEffectsAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid requestId,
        int expectedCount
    )
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM paguai.requests WHERE company_id = @company_id AND request_id = @request_id),
                (SELECT count(*) FROM foundation.idempotency_records WHERE company_id = @company_id AND request_id = @request_id AND command_name = 'paguai.draft.create'),
                (SELECT count(*) FROM audit.audit_events WHERE requested_company_id = @company_id AND action = 'paguai.draft.create')
            """,
            connection
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedCount, reader.GetInt64(0));
        Assert.Equal(expectedCount, reader.GetInt64(1));
        Assert.Equal(expectedCount, reader.GetInt64(2));
    }

    private static async Task<Scenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        int approverCount,
        bool approveTemplate
    )
    {
        var actorId = factory.ActorUserId;
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), $"paguai-group-{requestId:N}", actorId, requestId);
        var companyId = Guid.NewGuid();
        var foreignCompanyId = Guid.NewGuid();
        var approverIds = Enumerable.Range(0, approverCount).Select(_ => Guid.NewGuid()).ToArray();
        var foreignApproverId = Guid.NewGuid();
        var company = new Company(
            companyId,
            group.Id,
            $"paguai-company-{requestId:N}",
            actorId,
            requestId
        );
        var foreignCompany = new Company(
            foreignCompanyId,
            group.Id,
            $"paguai-foreign-{requestId:N}",
            actorId,
            requestId
        );
        var requesterMembership = new Membership(
            Guid.NewGuid(),
            companyId,
            actorId,
            actorId,
            requestId
        );
        var requesterRole = new Role(Guid.NewGuid(), companyId, "requester", actorId, requestId);
        var approverRole = new Role(Guid.NewGuid(), companyId, "approver", actorId, requestId);
        var foreignRole = new Role(
            Guid.NewGuid(),
            foreignCompanyId,
            "foreign approver",
            actorId,
            requestId
        );

        await using var database = factory.Fixture.CreateAdministratorDbContext();
        var readCapability = await database.Capabilities.SingleAsync(capability =>
            capability.Name == KnownCapabilities.PaguaiDraftRead
        );
        var createCapability = await database.Capabilities.SingleAsync(capability =>
            capability.Name == KnownCapabilities.PaguaiDraftCreate
        );
        var approveCapability = await database.Capabilities.SingleAsync(capability =>
            capability.Name == KnownCapabilities.PaguaiDraftApprove
        );
        var approverUsers = approverIds
            .Select(id => new User(id, "opaque approver", $"{id:N}@example.test"))
            .ToArray();
        var approverMemberships = approverIds
            .Select(id => new Membership(Guid.NewGuid(), companyId, id, actorId, requestId))
            .ToArray();
        var foreignUser = new User(
            foreignApproverId,
            "foreign approver",
            $"{foreignApproverId:N}@example.test"
        );
        var foreignMembership = new Membership(
            Guid.NewGuid(),
            foreignCompanyId,
            foreignApproverId,
            actorId,
            requestId
        );
        database.AddRange(group, company, foreignCompany, requesterMembership, requesterRole, approverRole, foreignRole);
        database.AddRange(approverUsers);
        database.AddRange(approverMemberships);
        database.AddRange(foreignUser, foreignMembership);
        database.AddRange(
            new RoleCapability(Guid.NewGuid(), companyId, requesterRole.Id, readCapability.Id, CapabilityScope.Company, actorId, requestId),
            new RoleCapability(Guid.NewGuid(), companyId, requesterRole.Id, createCapability.Id, CapabilityScope.Company, actorId, requestId),
            new RoleCapability(Guid.NewGuid(), companyId, requesterRole.Id, approveCapability.Id, CapabilityScope.Company, actorId, requestId),
            new RoleCapability(Guid.NewGuid(), companyId, approverRole.Id, approveCapability.Id, CapabilityScope.Company, actorId, requestId),
            new RoleCapability(Guid.NewGuid(), foreignCompanyId, foreignRole.Id, approveCapability.Id, CapabilityScope.Company, actorId, requestId),
            new RoleAssignment(Guid.NewGuid(), companyId, requesterRole.Id, requesterMembership.Id, actorId, requestId),
            new RoleAssignment(Guid.NewGuid(), foreignCompanyId, foreignRole.Id, foreignMembership.Id, actorId, requestId)
        );
        foreach (var membership in approverMemberships)
        {
            database.Add(
                new RoleAssignment(
                    Guid.NewGuid(),
                    companyId,
                    approverRole.Id,
                    membership.Id,
                    actorId,
                    requestId
                )
            );
        }

        await database.SaveChangesAsync();
        await database.Database.ExecuteSqlAsync(
            $"INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved) VALUES ({companyId}, 'in_app', {TemplateKey}, {approveTemplate})"
        );
        return new Scenario(companyId, approverIds, foreignApproverId);
    }

    private static void AssertOpaque(PaguaiEnvelope envelope, params string[] sensitiveValues)
    {
        var headers = string.Join('\n', envelope.Headers.Select(header => $"{header.Key}={header.Value}"));
        foreach (var value in sensitiveValues)
        {
            Assert.True(envelope.RawBody.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) < 0);
            Assert.DoesNotContain(value, envelope.RawMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain(value, headers, StringComparison.Ordinal);
        }
    }

    private sealed record Scenario(
        Guid CompanyId,
        IReadOnlyList<Guid> ApproverUserIds,
        Guid ForeignApproverId
    );

    private sealed record PaguaiEnvelope(
        Guid CompanyId,
        Guid DraftId,
        Guid RequesterUserId,
        IReadOnlyList<string> PayloadPropertyNames,
        string CorrelationId,
        IReadOnlyDictionary<string, string> Headers,
        byte[] RawBody,
        string RawMetadata
    );

    private sealed record IntentRow(
        Guid CompanyId,
        Guid RecipientUserId,
        string Channel,
        string EventKey,
        string TemplateKey,
        string DeepLinkSelector
    );

    private sealed record PaguaiDeadLetter(byte[] Body, string ExceptionType, string ExceptionMessage);
}
