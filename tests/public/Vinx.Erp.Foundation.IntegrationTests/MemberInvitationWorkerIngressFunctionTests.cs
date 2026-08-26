using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MemberInvitationWorkerIngressFunctionTests
{
    [Fact]
    public async Task Ingress_transitions_once_and_audits()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/invitations",
            new
            {
                requestId = Guid.NewGuid(),
                displayName = "Ingress Member",
                email = $"ingress-{Guid.NewGuid():N}@example.test",
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var invitationId = document.RootElement.GetProperty("invitationId").GetGuid();
        var correlationId = Guid.NewGuid();

        var first = await AcceptDeliveryAsync(factory.Fixture, invitationId, correlationId);
        Assert.Equal("pending_provider", first.Status);
        Assert.NotNull(first.ProviderPendingAt);
        Assert.True(first.Transitioned);

        var replay = await AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid());
        Assert.Equal("pending_provider", replay.Status);
        Assert.Equal(first.ProviderPendingAt, replay.ProviderPendingAt);
        Assert.False(replay.Transitioned);

        await AssertIngressAuditAsync(
            factory.Fixture,
            invitationId,
            scenario.CompanyId,
            factory.ActorUserId,
            correlationId
        );
    }

    [Fact]
    public async Task Ingress_fails_closed_when_the_membership_is_no_longer_active()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/invitations",
            new
            {
                requestId = Guid.NewGuid(),
                displayName = "Deleted Member",
                email = $"deleted-{Guid.NewGuid():N}@example.test",
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var invitationId = document.RootElement.GetProperty("invitationId").GetGuid();
        var membershipId = document.RootElement.GetProperty("membershipId").GetGuid();

        await SoftDeleteMembershipAsync(factory, membershipId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid())
        );

        Assert.Equal("23514", exception.SqlState);
        await AssertInvitationStateAsync(factory.Fixture, invitationId, "pending_delivery", null);
        Assert.Equal(0, await CountAuditsForInvitationAsync(factory.Fixture, invitationId));
    }

    private static async Task<DeliveryState> AcceptDeliveryAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid correlationId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, provider_pending_at, transitioned
            FROM foundation.accept_member_invitation_delivery(@invitation_id, @correlation_id)
            """,
            worker
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("correlation_id", correlationId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DeliveryState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetBoolean(2)
        );
    }

    private static async Task SoftDeleteMembershipAsync(
        FoundationApiFactory factory,
        Guid membershipId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE foundation.memberships SET deleted_at = clock_timestamp() WHERE id = @membership_id",
            administrator
        );
        command.Parameters.AddWithValue("membership_id", membershipId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertInvitationStateAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        string expectedStatus,
        DateTimeOffset? expectedProviderPendingAt
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, provider_pending_at
            FROM foundation.pending_member_invitations
            WHERE id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedStatus, reader.GetString(0));
        Assert.Equal(
            expectedProviderPendingAt,
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)
        );
    }

    private static async Task<int> CountAuditsForInvitationAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM audit.audit_events
            WHERE resource_id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertIngressAuditAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid expectedCompanyId,
        Guid expectedActorUserId,
        Guid expectedCorrelationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT source, action, scope, requested_company_id, actor_user_id,
                resource_type, resource_id, outcome, reason, metadata_json::text, correlation_id
            FROM audit.audit_events
            WHERE resource_id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("worker", reader.GetString(0));
        Assert.Equal("member_invitation.delivery.ingress", reader.GetString(1));
        Assert.Equal("company", reader.GetString(2));
        Assert.Equal(expectedCompanyId, reader.GetGuid(3));
        Assert.Equal(expectedActorUserId, reader.GetGuid(4));
        Assert.Equal("member_invitation", reader.GetString(5));
        Assert.Equal(invitationId, reader.GetGuid(6));
        Assert.Equal("success", reader.GetString(7));
        Assert.True(reader.IsDBNull(8));
        Assert.Equal("{}", reader.GetString(9));
        Assert.Equal(expectedCorrelationId, reader.GetGuid(10));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<IngressScenario> SeedScenarioAsync(FoundationApiFactory factory)
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

        administrator.AddRange(group, company, actorMembership, adminRole, grant, assignment);
        await administrator.SaveChangesAsync();
        return new IngressScenario(company.Id);
    }

    private sealed record IngressScenario(Guid CompanyId);

    private sealed record DeliveryState(
        string Status,
        DateTimeOffset? ProviderPendingAt,
        bool Transitioned
    );
}
