using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Paguai;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Xunit;

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
        });

        await Task.Delay(500);
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
            SELECT company_id, recipient_user_id, channel, event_key, template_key
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
                    reader.GetString(4)
                )
            );
        }

        return rows;
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
        string TemplateKey
    );
}
