using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Permissions;
using Vinx.Erp.Foundation.Persistence;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Contrato do POST de convite de membro: reserva do gráfico (usuário,
/// membresia, funcionário) com convite pendente, idempotência, conflitos e
/// envelope durável opaco no outbox.
/// </summary>
public sealed class MemberInvitationEndpointTests
{
    [Fact]
    public async Task Invite_creates_reserved_graph_and_pending_invitation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(Guid.NewGuid(), "Bea Member", "bea-member@example.test")
        );

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var membershipId = createdBody.GetProperty("membershipId").GetGuid();
        var invitationId = createdBody.GetProperty("invitationId").GetGuid();
        Assert.Equal("pending_delivery", createdBody.GetProperty("invitationStatus").GetString());
        Assert.NotEqual(Guid.Empty, membershipId);
        Assert.NotEqual(Guid.Empty, invitationId);
        Assert.Equal(
            new[] { "membershipId", "invitationId", "invitationStatus" },
            createdBody.EnumerateObject().Select(property => property.Name).Order()
        );

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var invitation = await administrator.Database
            .SqlQueryRaw<InvitationState>(
                """
                SELECT id AS "Id", membership_id AS "MembershipId", user_id AS "UserId",
                       company_id AS "CompanyId", status::text AS "Status"
                FROM foundation.pending_member_invitations WHERE id = {0}
                """,
                invitationId)
            .SingleAsync();
        Assert.Equal(membershipId, invitation.MembershipId);
        Assert.Equal(scenario.CompanyId, invitation.CompanyId);
        Assert.Equal("pending_delivery", invitation.Status);
        var employee = await administrator.Employees.IgnoreQueryFilters().SingleAsync(candidate =>
            candidate.MembershipId == membershipId);
        Assert.Equal("Bea Member", employee.DisplayName);
        var user = await administrator.Users.SingleAsync(candidate => candidate.Id == invitation.UserId);
        Assert.Equal("bea-member@example.test", user.Email);
        var success = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Action == "foundation.member.invite" && auditEvent.Outcome == AuditOutcome.Success);
        Assert.Equal("member_invitation", success.ResourceType);
        Assert.Equal(invitationId, success.ResourceId);
        Assert.Equal(factory.ActorUserId, success.ActorUserId);
    }

    [Fact]
    public async Task Invite_idempotency_and_conflicts()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        using var created = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(requestId, "Bea Member", "bea-replay@example.test")
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var replay = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(requestId, "Bea Member", "bea-replay@example.test")
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.GetProperty("invitationId").GetGuid(),
            replayBody.GetProperty("invitationId").GetGuid()
        );
        Assert.Equal("pending_delivery", replayBody.GetProperty("invitationStatus").GetString());

        using var conflict = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(requestId, "Other Name", "other@example.test")
        );
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync()
        );

        using var duplicateEmail = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(Guid.NewGuid(), "Second Member", "bea-replay@example.test")
        );
        Assert.Equal(HttpStatusCode.Conflict, duplicateEmail.StatusCode);
        Assert.Equal(
            "{\"code\":\"member_conflict\"}",
            await duplicateEmail.Content.ReadAsStringAsync()
        );

        using var invalid = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(Guid.NewGuid(), "Invalid Member", "not-an-email")
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(
            "{\"code\":\"invalid_request\"}",
            await invalid.Content.ReadAsStringAsync()
        );

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CommandName == "foundation.member.invite")
        );
        Assert.Equal(
            1,
            await administrator.PendingMemberInvitationsViewCount()
        );
    }

    [Fact]
    public async Task Invite_publishes_redacted_durable_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var displayName = $"invited-{Guid.NewGuid():N}";

        using var created = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(requestId, displayName, "envelope-member@example.test")
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var invitationId = createdDocument.RootElement.GetProperty("invitationId").GetGuid();

        var messages = await ReadMemberInvitationMessagesAsync(factory);
        var message = Assert.Single(messages);

        var envelope = EnvelopeSerializer.Deserialize(message.Body);
        Assert.Equal("application/json", envelope.ContentType);
        using var payload = JsonDocument.Parse(envelope.Data);
        var properties = payload.RootElement.EnumerateObject().ToArray();
        var invitationIdProperty = Assert.Single(properties);
        Assert.Equal("invitationId", invitationIdProperty.Name);
        Assert.Equal(invitationId, invitationIdProperty.Value.GetGuid());
        var requested = Assert.IsType<MemberInvitationDeliveryRequested>(
            new SystemTextJsonSerializer(
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ).ReadFromData(typeof(MemberInvitationDeliveryRequested), envelope)
        );
        Assert.Equal(invitationId, requested.InvitationId);
        Assert.Contains(
            "MemberInvitationDeliveryRequested",
            message.Metadata,
            StringComparison.Ordinal
        );
        foreach (
            var forbidden in new[]
            {
                invitationId.ToString("D"),
                requestId.ToString("D"),
                displayName,
                "envelope-member@example.test",
                "displayName",
                "email",
                "payloadHash",
                scenario.CompanyId.ToString("D"),
            }
        )
        {
            Assert.DoesNotContain(forbidden, message.Metadata, StringComparison.OrdinalIgnoreCase);
        }

        using var replay = await client.PostAsJsonAsync(
            InviteRoute(scenario.CompanyId),
            InviteBody(requestId, displayName, "envelope-member@example.test")
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Single(await ReadMemberInvitationMessagesAsync(factory));
    }

    private static string InviteRoute(Guid companyId) =>
        $"/foundation/companies/{companyId}/invitations";

    private static object InviteBody(Guid requestId, string displayName, string email) =>
        new { requestId, displayName, email };

    private static async Task<InvitationScenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        // O usuário do ator já é semeado pela factory autenticada.
        var actorMembership = new Membership(
            Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var adminRole = new Role(Guid.NewGuid(), company.Id, "Administrator", factory.ActorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign);
        var grant = new RoleCapability(
            Guid.NewGuid(), company.Id, adminRole.Id, capability.Id, CapabilityScope.Company,
            factory.ActorUserId, requestId);
        var assignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, adminRole.Id, actorMembership.Id, factory.ActorUserId, requestId);

        administrator.AddRange(
            group, company, actorMembership, adminRole, grant, assignment);
        await administrator.SaveChangesAsync();
        return new InvitationScenario(company.Id);
    }

    private static async Task<IReadOnlyList<MemberInvitationMessage>> ReadMemberInvitationMessagesAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var exists = new NpgsqlCommand(
            "SELECT to_regclass('vinx_wolverine_queues.wolverine_queue_member_invitation_delivery')::text",
            administrator
        );
        Assert.True(
            await exists.ExecuteScalarAsync() is string,
            "Wolverine member-invitation-delivery storage must be provisioned by deploy before message assertions; tests never create it."
        );

        await using var command = new NpgsqlCommand(
            "SELECT body, row_to_json(queue)::text FROM vinx_wolverine_queues.wolverine_queue_member_invitation_delivery AS queue",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        var messages = new List<MemberInvitationMessage>();
        while (await reader.ReadAsync())
        {
            messages.Add(new MemberInvitationMessage(
                reader.GetFieldValue<byte[]>(0), reader.GetString(1)));
        }

        return messages;
    }

    private sealed record InvitationScenario(Guid CompanyId);

    private sealed record MemberInvitationMessage(byte[] Body, string Metadata);

    private sealed record InvitationState(
        Guid Id,
        Guid MembershipId,
        Guid UserId,
        Guid CompanyId,
        string Status);
}

internal static class MemberInvitationTestExtensions
{
    // Leitura bruta da reserva (sem entidade mapeada): a tabela pertence ao fluxo
    // de entrega e não é exposta pelo DbContext.
    public static async Task<int> PendingMemberInvitationsViewCount(this FoundationDbContext database) =>
        await database
            .Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM foundation.pending_member_invitations")
            .SingleAsync();
}
