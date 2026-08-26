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

public sealed class MemberInvitationProviderDeliveryFunctionTests
{
    [Theory]
    [InlineData("success", "pending_acceptance", "success", null)]
    [InlineData("failed", "provider_failed", "failure", "provider_rejected")]
    [InlineData("ambiguous", "provider_ambiguous", "unknown", "provider_ambiguous")]
    public async Task Claim_and_finalize_mirror_owner_state_machine(
        string outcome,
        string expectedStatus,
        string expectedAuditOutcome,
        string? expectedReason
    )
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var (companyId, invitationId, memberEmail) = await InviteMemberAsync(factory);
        var correlationId = Guid.NewGuid();
        await MoveToPendingProviderAsync(factory.Fixture, invitationId, correlationId);

        var first = await ClaimAsync(factory.Fixture, invitationId, Guid.NewGuid());
        Assert.Equal("provider_in_flight", first.Status);
        Assert.NotEqual(Guid.Empty, first.AttemptId);
        Assert.Equal(memberEmail, first.MemberEmail);
        Assert.True(first.Claimed);

        var replay = await ClaimAsync(factory.Fixture, invitationId, Guid.NewGuid());
        Assert.Equal("provider_in_flight", replay.Status);
        Assert.Equal(first.AttemptId, replay.AttemptId);
        Assert.Null(replay.MemberEmail);
        Assert.False(replay.Claimed);

        var result = await FinalizeAsync(
            factory.Fixture,
            invitationId,
            first.AttemptId,
            correlationId,
            outcome,
            outcome == "success" ? "https://project.supabase.test/auth/v1" : null,
            outcome == "success" ? Guid.Parse("2a0399f0-a602-4d1f-8f79-22f4bf75f190") : null,
            expectedReason
        );

        Assert.Equal(expectedStatus, result.Status);
        Assert.True(result.Finalized);

        var finalizeReplay = await FinalizeAsync(
            factory.Fixture,
            invitationId,
            first.AttemptId,
            correlationId,
            outcome,
            outcome == "success" ? "https://project.supabase.test/auth/v1" : null,
            outcome == "success" ? Guid.Parse("2a0399f0-a602-4d1f-8f79-22f4bf75f190") : null,
            expectedReason
        );

        Assert.Equal(expectedStatus, finalizeReplay.Status);
        Assert.False(finalizeReplay.Finalized);
        await AssertFinalizationAsync(
            factory.Fixture,
            invitationId,
            companyId,
            memberEmail,
            expectedStatus,
            outcome == "success" ? "https://project.supabase.test/auth/v1" : null,
            outcome == "success" ? Guid.Parse("2a0399f0-a602-4d1f-8f79-22f4bf75f190") : null,
            expectedReason,
            expectedAuditOutcome
        );
    }

    [Fact]
    public async Task Finalize_failure_states_and_function_security()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT function_row.proname,
                   pg_get_userbyid(function_row.proowner),
                   function_row.prosecdef,
                   function_row.proconfig,
                   has_function_privilege('vinx_worker', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('public', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('vinx_migrator', function_row.oid, 'EXECUTE'),
                   NOT EXISTS (
                       SELECT 1
                       FROM aclexplode(COALESCE(
                           function_row.proacl,
                           acldefault('f', function_row.proowner))) AS privilege
                       WHERE privilege.privilege_type = 'EXECUTE'
                         AND privilege.grantee NOT IN (
                             function_row.proowner,
                             (SELECT oid FROM pg_roles WHERE rolname = 'vinx_worker')))
            FROM pg_proc AS function_row
            WHERE function_row.oid IN (
                'foundation.claim_member_invitation_provider_delivery(uuid,uuid)'::regprocedure,
                'foundation.finalize_member_invitation_provider_delivery(uuid,uuid,uuid,text,text,uuid,text)'::regprocedure)
            ORDER BY function_row.proname;
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        var functionNames = new List<string>();
        while (await reader.ReadAsync())
        {
            functionNames.Add(reader.GetString(0));
            Assert.Equal("vinx_worker_owner", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.Equal(
                ["search_path=pg_catalog, foundation, pg_temp"],
                reader.GetFieldValue<string[]>(3)
            );
            Assert.True(reader.GetBoolean(4));
            Assert.True(reader.GetBoolean(5));
            Assert.True(reader.GetBoolean(6));
            Assert.True(reader.GetBoolean(7));
            Assert.True(reader.GetBoolean(8));
        }

        Assert.Equal(
            [
                "claim_member_invitation_provider_delivery",
                "finalize_member_invitation_provider_delivery",
            ],
            functionNames
        );

        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var (_, invitationId, _) = await InviteMemberAsync(factory);
        var correlationId = Guid.NewGuid();
        await MoveToPendingProviderAsync(factory.Fixture, invitationId, correlationId);
        var claim = await ClaimAsync(factory.Fixture, invitationId, correlationId);

        // Sucesso sem subject válido é rejeitado e não avança a máquina de estado.
        var emptySubject = await Assert.ThrowsAsync<PostgresException>(() =>
            FinalizeAsync(
                factory.Fixture,
                invitationId,
                claim.AttemptId,
                correlationId,
                "success",
                "https://project.supabase.test/auth/v1",
                Guid.Empty,
                null
            )
        );
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptySubject.SqlState);

        await using (var adminConnection = await factory.Fixture.OpenAdministratorConnectionAsync())
        await using (
            var revoke = new NpgsqlCommand(
                "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_worker_owner",
                adminConnection
            )
        )
        {
            await revoke.ExecuteNonQueryAsync();
        }

        var auditUnavailable = await Assert.ThrowsAsync<PostgresException>(() =>
            FinalizeAsync(
                factory.Fixture,
                invitationId,
                claim.AttemptId,
                correlationId,
                "success",
                "https://project.supabase.test/auth/v1",
                Guid.Parse("2a0399f0-a602-4d1f-8f79-22f4bf75f190"),
                null
            )
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, auditUnavailable.SqlState);
        await AssertInFlightWithoutProviderAuditAsync(factory.Fixture, invitationId);
    }

    private static async Task<(Guid CompanyId, Guid InvitationId, string MemberEmail)> InviteMemberAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Provider Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Provider Company", factory.ActorUserId, requestId);
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

        var memberEmail = $"provider-member-{Guid.NewGuid():N}@example.test";
        using var created = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{company.Id}/invitations",
                new
                {
                    requestId = Guid.NewGuid(),
                    displayName = "Provider Member",
                    email = memberEmail,
                }
            );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return (
            company.Id,
            document.RootElement.GetProperty("invitationId").GetGuid(),
            memberEmail
        );
    }

    private static async Task MoveToPendingProviderAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid correlationId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM foundation.accept_member_invitation_delivery(@invitation_id, @correlation_id)",
            worker
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ProviderClaim> ClaimAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid correlationId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, attempt_id, member_email, claimed
            FROM foundation.claim_member_invitation_provider_delivery(@invitation_id, @correlation_id)
            """,
            worker
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProviderClaim(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3)
        );
    }

    private static async Task<ProviderFinalization> FinalizeAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid attemptId,
        Guid correlationId,
        string outcome,
        string? issuer,
        Guid? subject,
        string? failureReason
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, finalized
            FROM foundation.finalize_member_invitation_provider_delivery(
                @invitation_id, @attempt_id, @correlation_id, @outcome, @issuer, @subject, @failure_reason)
            """,
            worker
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("attempt_id", attemptId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("issuer", (object?)issuer ?? DBNull.Value);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("failure_reason", (object?)failureReason ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProviderFinalization(reader.GetString(0), reader.GetBoolean(1));
    }

    private static async Task AssertFinalizationAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid expectedCompanyId,
        string memberEmail,
        string expectedStatus,
        string? expectedIssuer,
        Guid? expectedSubject,
        string? expectedReason,
        string expectedAuditOutcome
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT invitation.status,
                   invitation.provider_issuer,
                   invitation.provider_subject,
                   invitation.provider_failure_reason,
                   (SELECT count(*) FROM audit.audit_events AS audit_event
                    WHERE audit_event.resource_id = invitation.id
                      AND audit_event.action = 'member_invitation.delivery.provider'
                      AND audit_event.outcome = @audit_outcome
                      AND audit_event.reason IS NOT DISTINCT FROM @reason
                      AND audit_event.requested_company_id = @company_id
                      AND audit_event.metadata_json::text NOT LIKE '%' || @member_email || '%')
            FROM foundation.pending_member_invitations AS invitation
            WHERE invitation.id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("audit_outcome", expectedAuditOutcome);
        command.Parameters.AddWithValue("reason", (object?)expectedReason ?? DBNull.Value);
        command.Parameters.AddWithValue("company_id", expectedCompanyId);
        command.Parameters.AddWithValue("member_email", memberEmail);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedStatus, reader.GetString(0));
        Assert.Equal(expectedIssuer, reader.IsDBNull(1) ? null : reader.GetString(1));
        Assert.Equal(expectedSubject, reader.IsDBNull(2) ? null : reader.GetGuid(2));
        Assert.Equal(expectedReason, reader.IsDBNull(3) ? null : reader.GetString(3));
        Assert.Equal(1L, reader.GetInt64(4));
    }

    private static async Task AssertInFlightWithoutProviderAuditAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT invitation.status,
                   invitation.provider_completed_at IS NULL,
                   invitation.provider_issuer IS NULL,
                   invitation.provider_subject IS NULL,
                   invitation.provider_failure_reason IS NULL,
                   (SELECT count(*) FROM audit.audit_events AS audit_event
                    WHERE audit_event.resource_id = invitation.id
                      AND audit_event.action = 'member_invitation.delivery.provider')
            FROM foundation.pending_member_invitations AS invitation
            WHERE invitation.id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("provider_in_flight", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(0L, reader.GetInt64(5));
    }

    private sealed record ProviderClaim(
        string Status,
        Guid AttemptId,
        string? MemberEmail,
        bool Claimed
    );

    private sealed record ProviderFinalization(string Status, bool Finalized);
}
