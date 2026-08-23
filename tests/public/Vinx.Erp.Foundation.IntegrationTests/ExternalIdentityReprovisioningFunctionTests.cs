using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ExternalIdentityReprovisioningFunctionTests
{
    private const string BeforeExternalIdentityReprovisioning = "20260821160000_AddPaguaiDraftSourceDocument";
    private const string Issuer = "https://issuer.reprovisioning.test";
    private const string OldSubject = "subject-before-reprovisioning";
    private const string NewSubject = "subject-after-reprovisioning";

    [Fact]
    public async Task Reprovisioning_exact_identity_rotates_subject_revokes_sessions_and_preserves_authorization_graph()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedExactIdentityAsync(fixture);

        var result = await ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            OldSubject,
            NewSubject,
            Guid.NewGuid()
        );

        Assert.Equal("reprovisioned", result.Status);

        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(seed.UserId, state.IdentityUserId);
        Assert.Equal(seed.IdentityId, state.IdentityId);
        Assert.Equal(NewSubject, state.Subject);
        Assert.NotNull(state.SessionsValidAfter);
        Assert.Equal(2, state.RevokedSessions);
        Assert.Equal(1, state.RevocationTimestamps);
        Assert.True(state.SessionsRevokedForIdentityRecovery);
        Assert.Equal(seed.AuthorizationGraph, state.AuthorizationGraph);
        Assert.Equal(seed.IdentityId, state.PlatformOperatorIdentityId);
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal("global", state.AuditScope);
        Assert.Equal("identity.external.reprovision", state.AuditAction);
        Assert.Equal("success", state.AuditOutcome);
        Assert.Equal("same_person_recovery", state.AuditReason);
        Assert.Equal(seed.UserId, state.AuditActorUserId);
        Assert.Equal(seed.IdentityId, state.AuditResourceId);
        Assert.NotNull(state.AuditMetadata);
        Assert.Empty(JsonDocument.Parse(state.AuditMetadata!).RootElement.EnumerateObject());

        await AssertFreshIdentityResolutionAsync(fixture, seed.UserId, state.SessionsValidAfter!.Value);
    }

    [Fact]
    public async Task Reprovisioning_function_is_migrator_only_and_runtime_and_public_cannot_mutate_identity_or_session_state()
    {
        await using var fixture = await StartFixtureAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pg_get_userbyid(reprovisioning_function.proowner),
                reprovisioning_function.prosecdef,
                reprovisioning_function.proconfig,
                has_function_privilege(
                    'vinx_migrator',
                    'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)',
                    'EXECUTE'),
                has_function_privilege(
                    'public',
                    'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)',
                    'EXECUTE'),
                has_column_privilege('vinx_runtime', 'foundation.external_identities', 'subject', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.user_sessions', 'revoked_at', 'UPDATE'),
                has_column_privilege('public', 'foundation.external_identities', 'subject', 'UPDATE'),
                has_column_privilege('public', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('public', 'foundation.user_sessions', 'revoked_at', 'UPDATE')
            FROM pg_proc AS reprovisioning_function
            WHERE reprovisioning_function.oid =
                'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)'::regprocedure
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("vinx_identity_owner", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.True(reader.GetBoolean(3));
        for (var index = 4; index < reader.FieldCount; index++)
        {
            Assert.False(reader.GetBoolean(index));
        }
    }

    [Fact]
    public async Task Reprovisioning_rejects_tuple_mismatch_and_new_subject_already_linked_without_mutation()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedExactIdentityAsync(fixture);
        var conflictingUserId = Guid.NewGuid();
        var conflictingIdentityId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var conflict = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'Conflicting user', 'conflicting-user@example.test', clock_timestamp());
            INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
            VALUES (@identity_id, @user_id, @issuer, @subject, clock_timestamp());
            """,
            administrator))
        {
            conflict.Parameters.AddWithValue("user_id", conflictingUserId);
            conflict.Parameters.AddWithValue("identity_id", conflictingIdentityId);
            conflict.Parameters.AddWithValue("issuer", Issuer);
            conflict.Parameters.AddWithValue("subject", NewSubject);
            await conflict.ExecuteNonQueryAsync();
        }

        var tupleMismatch = await ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            "wrong-old-subject",
            "another-subject",
            Guid.NewGuid()
        );
        var existingNewSubject = await ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            OldSubject,
            NewSubject,
            Guid.NewGuid()
        );

        Assert.Equal("conflict", tupleMismatch.Status);
        Assert.Equal("conflict", existingNewSubject.Status);
        await AssertNoReprovisioningMutationAsync(fixture, seed);
    }

    [Fact]
    public async Task Reprovisioning_rolls_back_identity_and_session_state_when_success_audit_cannot_be_appended()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedExactIdentityAsync(fixture);
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var revoke = new NpgsqlCommand(
            """
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_identity_owner
            """,
            administrator))
        {
            await revoke.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            OldSubject,
            NewSubject,
            Guid.NewGuid()
        ));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await AssertNoReprovisioningMutationAsync(fixture, seed);
    }

    [Fact]
    public async Task Reprovisioning_replay_returns_already_configured_without_duplicate_audit_or_session_transition()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedExactIdentityAsync(fixture);
        var first = await ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            OldSubject,
            NewSubject,
            Guid.NewGuid()
        );
        var afterFirst = await ReadStateAsync(fixture, seed);

        var replay = await ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            OldSubject,
            NewSubject,
            Guid.NewGuid()
        );
        var afterReplay = await ReadStateAsync(fixture, seed);

        Assert.Equal("reprovisioned", first.Status);
        Assert.Equal("already_configured", replay.Status);
        Assert.Equal(1, afterReplay.SuccessAudits);
        Assert.Equal(afterFirst.SessionsValidAfter, afterReplay.SessionsValidAfter);
        Assert.Equal(afterFirst.RevokedSessions, afterReplay.RevokedSessions);
    }

    [Fact]
    public async Task Reprovisioning_concurrent_new_subjects_have_one_winner_and_leave_no_partial_loser_state()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedExactIdentityAsync(fixture);
        var firstSubject = "subject-after-reprovisioning-first";
        var secondSubject = "subject-after-reprovisioning-second";

        var results = await Task.WhenAll(
            ReprovisionAsMigratorAsync(
                fixture, seed.UserId, seed.IdentityId, Issuer, OldSubject, firstSubject, Guid.NewGuid()),
            ReprovisionAsMigratorAsync(
                fixture, seed.UserId, seed.IdentityId, Issuer, OldSubject, secondSubject, Guid.NewGuid())
        );

        Assert.Single(results, result => result.Status == "reprovisioned");
        Assert.Single(results, result => result.Status == "conflict");
        var state = await ReadStateAsync(fixture, seed);
        Assert.Contains(state.Subject, new[] { firstSubject, secondSubject });
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal(2, state.RevokedSessions);
    }

    [Fact]
    public async Task Reprovisioning_downgrade_fails_closed_after_the_contract_is_used()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedExactIdentityAsync(fixture);
        var result = await ReprovisionAsMigratorAsync(
            fixture,
            seed.UserId,
            seed.IdentityId,
            Issuer,
            OldSubject,
            NewSubject,
            Guid.NewGuid()
        );
        Assert.Equal("reprovisioned", result.Status);

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeExternalIdentityReprovisioning)
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(NewSubject, state.Subject);
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal(2, state.RevokedSessions);
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<ReprovisioningSeed> SeedExactIdentityAsync(FoundationPostgresFixture fixture)
    {
        var userId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var capabilityId = Guid.NewGuid();
        var roleCapabilityId = Guid.NewGuid();
        var roleAssignmentId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'Recovery user', 'recovery-user@example.test', clock_timestamp());
            INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
            VALUES (@identity_id, @user_id, @issuer, @old_subject, clock_timestamp());
            INSERT INTO foundation.groups (
                id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                @group_id, 'Recovery group', @user_id, @user_id, @request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (
                id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                @company_id, @group_id, 'Recovery company', @user_id, @user_id, @request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.memberships (
                id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                @membership_id, @company_id, @user_id, @user_id, @user_id, @request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.roles (
                id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                @role_id, @company_id, 'Recovery role', @user_id, @user_id, @request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.capabilities (id, name, description, created_at)
            VALUES (@capability_id, 'reprovisioning.graph.assertion', 'Test graph capability', clock_timestamp());
            INSERT INTO foundation.role_capabilities (
                id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id,
                request_id, created_at, updated_at)
            VALUES (
                @role_capability_id, @company_id, @role_id, @capability_id, 'company', @user_id, @user_id,
                @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_assignments (
                id, company_id, role_id, membership_id, created_by_user_id, updated_by_user_id,
                request_id, created_at, updated_at)
            VALUES (
                @role_assignment_id, @company_id, @role_id, @membership_id, @user_id, @user_id,
                @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            VALUES (1, @identity_id);
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at)
            VALUES
                (gen_random_uuid(), @user_id, @issuer, gen_random_uuid(),
                    '2026-08-23T12:00:00+00:00', '2026-08-23T13:00:00+00:00'),
                (gen_random_uuid(), @user_id, @issuer, gen_random_uuid(),
                    '2026-08-23T12:01:00+00:00', '2026-08-23T13:01:00+00:00');
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("identity_id", identityId);
        command.Parameters.AddWithValue("issuer", Issuer);
        command.Parameters.AddWithValue("old_subject", OldSubject);
        command.Parameters.AddWithValue("group_id", groupId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("role_id", roleId);
        command.Parameters.AddWithValue("capability_id", capabilityId);
        command.Parameters.AddWithValue("role_capability_id", roleCapabilityId);
        command.Parameters.AddWithValue("role_assignment_id", roleAssignmentId);
        command.Parameters.AddWithValue("request_id", requestId);
        await command.ExecuteNonQueryAsync();

        return new ReprovisioningSeed(
            userId,
            identityId,
            $"{membershipId}|{roleId}|{capabilityId}|{roleCapabilityId}|{roleAssignmentId}"
        );
    }

    private static async Task<ReprovisioningResult> ReprovisionAsMigratorAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        Guid identityId,
        string issuer,
        string oldSubject,
        string newSubject,
        Guid correlationId
    )
    {
        await using var migrator = new NpgsqlConnection(fixture.MigratorConnectionString);
        await migrator.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status
            FROM foundation.reprovision_external_identity(
                @user_id, @identity_id, @issuer, @old_subject, @new_subject, @correlation_id)
            """,
            migrator
        );
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("identity_id", identityId);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("old_subject", oldSubject);
        command.Parameters.AddWithValue("new_subject", newSubject);
        command.Parameters.AddWithValue("correlation_id", correlationId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new ReprovisioningResult(reader.GetString(0));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task<ReprovisioningState> ReadStateAsync(
        FoundationPostgresFixture fixture,
        ReprovisioningSeed seed
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                identity_record.id,
                identity_record.user_id,
                identity_record.subject,
                user_record.sessions_valid_after,
                (SELECT count(*) FROM foundation.user_sessions
                    WHERE user_id = @user_id AND revoked_at IS NOT NULL),
                (SELECT count(DISTINCT revoked_at) FROM foundation.user_sessions
                    WHERE user_id = @user_id AND revoked_at IS NOT NULL),
                (SELECT coalesce(bool_and(
                    revoked_by_user_id = @user_id
                    AND revocation_reason = 'identity_reprovisioned'), false)
                    FROM foundation.user_sessions
                    WHERE user_id = @user_id),
                concat_ws('|',
                    (SELECT membership.id FROM foundation.memberships AS membership WHERE membership.id = @membership_id),
                    (SELECT role_record.id FROM foundation.roles AS role_record WHERE role_record.id = @role_id),
                    (SELECT capability.id FROM foundation.capabilities AS capability WHERE capability.id = @capability_id),
                    (SELECT role_capability.id FROM foundation.role_capabilities AS role_capability WHERE role_capability.id = @role_capability_id),
                    (SELECT role_assignment.id FROM foundation.role_assignments AS role_assignment WHERE role_assignment.id = @role_assignment_id)),
                (SELECT external_identity_id FROM foundation.platform_operators WHERE singleton = 1),
                (SELECT count(*) FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' AND outcome = 'success'),
                (SELECT scope FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT action FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT outcome FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT reason FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT actor_user_id FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT resource_id FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT metadata_json::text FROM audit.audit_events
                    WHERE action = 'identity.external.reprovision' ORDER BY occurred_at DESC LIMIT 1)
            FROM foundation.external_identities AS identity_record
            INNER JOIN foundation.users AS user_record ON user_record.id = identity_record.user_id
            WHERE identity_record.id = @identity_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", seed.UserId);
        command.Parameters.AddWithValue("identity_id", seed.IdentityId);
        command.Parameters.AddWithValue("membership_id", Guid.Parse(seed.AuthorizationGraph.Split('|')[0]));
        command.Parameters.AddWithValue("role_id", Guid.Parse(seed.AuthorizationGraph.Split('|')[1]));
        command.Parameters.AddWithValue("capability_id", Guid.Parse(seed.AuthorizationGraph.Split('|')[2]));
        command.Parameters.AddWithValue("role_capability_id", Guid.Parse(seed.AuthorizationGraph.Split('|')[3]));
        command.Parameters.AddWithValue("role_assignment_id", Guid.Parse(seed.AuthorizationGraph.Split('|')[4]));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ReprovisioningState(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<long>(4),
            reader.GetFieldValue<long>(5),
            reader.GetBoolean(6),
            reader.GetString(7),
            reader.GetGuid(8),
            reader.GetFieldValue<long>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetGuid(14),
            reader.IsDBNull(15) ? null : reader.GetGuid(15),
            reader.IsDBNull(16) ? null : reader.GetString(16)
        );
    }

    private static async Task AssertNoReprovisioningMutationAsync(
        FoundationPostgresFixture fixture,
        ReprovisioningSeed seed
    )
    {
        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(seed.UserId, state.IdentityUserId);
        Assert.Equal(seed.IdentityId, state.IdentityId);
        Assert.Equal(OldSubject, state.Subject);
        Assert.Null(state.SessionsValidAfter);
        Assert.Equal(0, state.RevokedSessions);
        Assert.Equal(seed.AuthorizationGraph, state.AuthorizationGraph);
        Assert.Equal(seed.IdentityId, state.PlatformOperatorIdentityId);
        Assert.Equal(0, state.SuccessAudits);
    }

    private static async Task AssertFreshIdentityResolutionAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        DateTimeOffset sessionsValidAfter
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using (var resolution = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM foundation.external_identities
                    WHERE issuer = @issuer AND subject = @old_subject),
                (SELECT user_id FROM foundation.external_identities
                    WHERE issuer = @issuer AND subject = @new_subject)
            """,
            administrator))
        {
            resolution.Parameters.AddWithValue("issuer", Issuer);
            resolution.Parameters.AddWithValue("old_subject", OldSubject);
            resolution.Parameters.AddWithValue("new_subject", NewSubject);
            await using var reader = await resolution.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.Equal(userId, reader.GetGuid(1));
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var freshSession = new NpgsqlCommand(
            """
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at)
            VALUES (
                gen_random_uuid(), @user_id, @issuer, gen_random_uuid(), @authenticated_at, @expires_at)
            """,
            runtime
        );
        freshSession.Parameters.AddWithValue("user_id", userId);
        freshSession.Parameters.AddWithValue("issuer", Issuer);
        freshSession.Parameters.AddWithValue("authenticated_at", sessionsValidAfter.AddMinutes(1));
        freshSession.Parameters.AddWithValue("expires_at", sessionsValidAfter.AddHours(1));
        Assert.Equal(1, await freshSession.ExecuteNonQueryAsync());
    }

    private sealed record ReprovisioningSeed(Guid UserId, Guid IdentityId, string AuthorizationGraph);

    private sealed record ReprovisioningResult(string Status);

    private sealed record ReprovisioningState(
        Guid IdentityId,
        Guid IdentityUserId,
        string Subject,
        DateTimeOffset? SessionsValidAfter,
        long RevokedSessions,
        long RevocationTimestamps,
        bool SessionsRevokedForIdentityRecovery,
        string AuthorizationGraph,
        Guid PlatformOperatorIdentityId,
        long SuccessAudits,
        string? AuditScope,
        string? AuditAction,
        string? AuditOutcome,
        string? AuditReason,
        Guid? AuditActorUserId,
        Guid? AuditResourceId,
        string? AuditMetadata
    );
}
