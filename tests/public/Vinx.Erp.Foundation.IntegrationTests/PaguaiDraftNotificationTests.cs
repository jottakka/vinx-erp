using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Paguai;
using Wolverine;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Faixa B: primeiro produtor real de notificações. A criação do rascunho
/// publica PaguaiDraftCreated no outbox durável NA MESMA transação; o worker
/// resolve destinatários com paguai.draft.approve na entrega e enfileira a
/// intenção in-app por destinatário (falha é WARNING best-effort).
/// </summary>
public sealed class PaguaiDraftNotificationTests
{
    [Fact]
    public async Task Draft_create_enrolls_durable_event_envelope_in_the_same_commit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            Body(scenario.CompanyId)
        );

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var count = await CountDurablePaguaiMessagesAsync(factory);
        Assert.True(count >= 1);

        // Envelope opaco: nenhum dado financeiro ou PII do rascunho sai no corpo.
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT body, metadata FROM vinx_wolverine.wolverine_outgoing_envelopes",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        var found = false;
        while (await reader.ReadAsync())
        {
            var body = reader.GetString(0);
            if (!body.Contains(nameof(PaguaiDraftCreated), StringComparison.Ordinal))
            {
                continue;
            }

            found = true;
            var metadata = reader.GetString(1);
            foreach (var forbidden in new[] { "Office supplies", "1250", "Fornecedor Demo" })
            {
                Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, metadata, StringComparison.Ordinal);
            }
        }

        Assert.True(found);
    }

    [Fact]
    public async Task Outbox_unavailable_fails_closed_and_keeps_no_draft()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        // Item 9 da revisão: o grant é restaurado em finally para não vazar
        // revogação para outros testes do pool.
        try
        {
            await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
            await using (var revoke = new NpgsqlCommand(
                "REVOKE INSERT ON vinx_wolverine.wolverine_outgoing_envelopes FROM vinx_runtime;",
                administrator))
            {
                await revoke.ExecuteNonQueryAsync();
            }

            using var failed = await client.PostAsJsonAsync("/paguai/requests", Body(scenario.CompanyId));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            await using var guard = await factory.Fixture.OpenAdministratorConnectionAsync();
            Assert.Equal(
                0,
                (long)(await new NpgsqlCommand(
                    "SELECT count(*) FROM paguai.requests WHERE company_id = "
                        + $"'{scenario.CompanyId}'::uuid",
                    guard
                ).ExecuteScalarAsync())!
            );
        }
        finally
        {
            await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
            await using var restore = new NpgsqlCommand(
                "GRANT INSERT ON vinx_wolverine.wolverine_outgoing_envelopes TO vinx_runtime;",
                administrator
            );
            await restore.ExecuteNonQueryAsync();
        }

        using var retry = await client.PostAsJsonAsync("/paguai/requests", Body(scenario.CompanyId));
        // Com o canal restaurado, o MESMO payload cria e publica envelope novo.
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(await CountDurablePaguaiMessagesAsync(factory) >= 1);
    }

    [Fact]
    public async Task Handler_resolves_approvers_dispatch_time_and_enqueues_intent_excluding_creator()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var approver = await SeedApproverAsync(factory, scenario.CompanyId, templateApproved: true);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/paguai/requests", Body(scenario.CompanyId));
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = Guid.Parse(document.RootElement.GetProperty("id").GetString()!);

        using var serviceScope = factory.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<NotificationIntentService>();
        await using var directoryContext = factory.Fixture.CreateAdministratorDbContext();
        var directory = new PaguaiApproverDirectory(directoryContext);
        var handler = new PaguaiDraftCreatedHandler(service, directory, NullLogger());
        var message = new PaguaiDraftCreated(scenario.CompanyId, draftId, factory.ActorUserId);
        var envelope = new Envelope(message)
        {
            CorrelationId = Guid.NewGuid().ToString("D"),
        };

        await handler.Handle(message, envelope, CancellationToken.None);
        // Replay determinístico: segunda entrega do MESMO evento não duplica.
        await handler.Handle(message, envelope, CancellationToken.None);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var intents = await ReadIntentsAsync(administrator);
        var intent = Assert.Single(intents);
        Assert.Equal(approver.UserId, intent.RecipientUserId);
        Assert.NotEqual(factory.ActorUserId, intent.RecipientUserId);
        Assert.Equal(PaguaiNotificationKeys.DraftCreatedEventKey(draftId), intent.EventKey);
        Assert.Equal("pending_delivery", intent.Status);
    }

    [Fact]
    public async Task Missing_template_defers_without_intent_and_without_failure()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        await SeedApproverAsync(factory, scenario.CompanyId, templateApproved: false);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/paguai/requests", Body(scenario.CompanyId));
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = Guid.Parse(document.RootElement.GetProperty("id").GetString()!);

        using var serviceScope = factory.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<NotificationIntentService>();
        await using var directoryContext = factory.Fixture.CreateAdministratorDbContext();
        var directory = new PaguaiApproverDirectory(directoryContext);
        var handler = new PaguaiDraftCreatedHandler(service, directory, NullLogger());
        var message = new PaguaiDraftCreated(scenario.CompanyId, draftId, factory.ActorUserId);
        await handler.Handle(message, new Envelope(message), CancellationToken.None);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await ReadIntentsAsync(administrator));
    }

    private static Microsoft.Extensions.Logging.ILogger<PaguaiDraftCreatedHandler> NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<PaguaiDraftCreatedHandler>.Instance;

    private static async Task<List<IntentRow>> ReadIntentsAsync(
        FoundationDbContext database)
    {
        await using var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT recipient_user_id, event_key, status FROM notifications.notification_intents";
        var rows = new List<IntentRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new IntentRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private sealed record IntentRow(Guid RecipientUserId, string EventKey, string Status);

    private static object Body(Guid companyId) => new
    {
        companyId,
        requestId = Guid.NewGuid(),
        title = "Office supplies",
        description = "Replacement printer paper",
        amount = "1250.00",
        currency = "BRL",
        dueDate = "2026-09-01",
        beneficiaryDisplayName = "Fornecedor Demo",
        proofRequirement = "optional",
    };

    private static async Task<long> CountDurablePaguaiMessagesAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM vinx_wolverine.wolverine_outgoing_envelopes WHERE message_type LIKE '%PaguaiDraftCreated%')
              + (SELECT count(*) FROM vinx_wolverine_queues.wolverine_queue_paguai_events)
            """,
            administrator
        );
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Empresa com membership e capabilities read/create para o ator da fábrica.</summary>
    private static async Task<(Guid CompanyId, Guid MembershipId)> SeedScenarioAsync(
        FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Paguai requester", factory.ActorUserId, requestId);
        var readCapability = await EnsureCapabilityAsync(administrator,
            KnownCapabilities.PaguaiDraftReadId, KnownCapabilities.PaguaiDraftRead);
        var createCapability = await EnsureCapabilityAsync(administrator,
            KnownCapabilities.PaguaiDraftCreateId, KnownCapabilities.PaguaiDraftCreate);
        administrator.AddRange(
            group,
            company,
            membership,
            role,
            new RoleCapability(Guid.NewGuid(), company.Id, role.Id, readCapability.Id, CapabilityScope.Company, factory.ActorUserId, requestId),
            new RoleCapability(Guid.NewGuid(), company.Id, role.Id, createCapability.Id, CapabilityScope.Company, factory.ActorUserId, requestId),
            new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, requestId));
        await administrator.SaveChangesAsync();
        return (company.Id, membership.Id);
    }

    /// <summary>Aprovador com paguai.draft.approve + template in-app aprovável (ou não).</summary>
    private static async Task<(Guid UserId, Guid MembershipId)> SeedApproverAsync(
        FoundationApiFactory factory,
        Guid companyId,
        bool templateApproved)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var user = new User(userId, "Aprovadora", $"approver-{userId:N}@vinx.test");
        var membership = new Membership(Guid.NewGuid(), companyId, userId, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), companyId, "Paguai approver", factory.ActorUserId, requestId);
        var capability = await EnsureCapabilityAsync(administrator,
            KnownCapabilities.PaguaiDraftApproveId, KnownCapabilities.PaguaiDraftApprove);
        administrator.AddRange(
            user,
            membership,
            role,
            new RoleCapability(Guid.NewGuid(), companyId, role.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId),
            new RoleAssignment(Guid.NewGuid(), companyId, role.Id, membership.Id, factory.ActorUserId, requestId));

        administrator.Database.ExecuteSqlRaw(
            """
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES ({0}, 'in_app', {1}, {2})
            """,
            companyId,
            PaguaiNotificationKeys.DraftCreatedApproverTemplateKey,
            templateApproved);
        await administrator.SaveChangesAsync();
        return (userId, membership.Id);
    }

    private static async Task<Capability> EnsureCapabilityAsync(
        FoundationDbContext database,
        Guid id,
        string name)
    {
        // Catálogo já semeado pela migration: somente adicionar quando ausente.
        var existing = await database.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var created = new Capability(id, name);
        database.Capabilities.Add(created);
        return created;
    }
}
