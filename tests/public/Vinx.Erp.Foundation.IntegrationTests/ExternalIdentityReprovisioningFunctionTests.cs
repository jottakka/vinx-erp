using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ExternalIdentityReprovisioningFunctionTests
{
    private const string BeforeReprovisioning = "20260821160000_AddPaguaiDraftSourceDocument";
    private const string Issuer = "https://issuer.reprovisioning.test";
    private const string OldSubject = "subject-before-reprovisioning";
    private const string NewSubject = "subject-after-reprovisioning";
    private static readonly Guid DeploymentActorId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Reprovisioning_rotates_the_exact_identity_and_preserves_its_graph()
    {
        await using var fixture = await StartAsync();
        var seed = await SeedAsync(fixture);
        var correlationId = Guid.NewGuid();
        var authorizationBefore = await AuthorizationProjectionAsync(fixture, seed);
        Assert.Equal("reprovisioned", await RotateAsync(fixture, seed, NewSubject, correlationId));

        var state = await ReadAsync(fixture, seed);
        Assert.Equal((seed.UserId, seed.IdentityId, NewSubject), (state.UserId, state.IdentityId, state.Subject));
        Assert.NotNull(state.Barrier);
        Assert.Equal(2, state.RevokedSessions);
        Assert.True(state.RevokedForRecovery);
        Assert.Equal(seed.IdentityId, state.OperatorIdentityId);
        Assert.Equal(1, state.ReprovisioningAudits);
        Assert.Equal(DeploymentActorId, state.AuditActorId);
        Assert.Equal(seed.UserId, state.AuditResourceId);
        Assert.Equal(correlationId, state.AuditCorrelationId);
        Assert.Empty(JsonDocument.Parse(state.AuditMetadata!).RootElement.EnumerateObject());
        Assert.Equal(authorizationBefore, await AuthorizationProjectionAsync(fixture, seed));
        Assert.Equal(seed.UserId, await ResolveAsync(fixture, NewSubject));
        Assert.Null(await ResolveAsync(fixture, OldSubject));
        await InsertFreshSessionAsync(fixture, seed.UserId, state.Barrier!.Value);
    }

    [Fact]
    public async Task Reprovisioning_is_identity_owner_definer_and_migrator_only()
    {
        await using var fixture = await StartAsync();
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_userbyid(proowner), prosecdef, proconfig,
                has_function_privilege('vinx_migrator', 'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)', 'EXECUTE'),
                has_function_privilege('public', 'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)', 'EXECUTE'),
                has_column_privilege('vinx_runtime', 'foundation.external_identities', 'subject', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.user_sessions', 'revoked_at', 'UPDATE'),
                has_column_privilege('public', 'foundation.external_identities', 'subject', 'UPDATE'),
                has_column_privilege('public', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('public', 'foundation.user_sessions', 'revoked_at', 'UPDATE')
            FROM pg_proc
            WHERE oid = 'foundation.reprovision_external_identity(uuid,uuid,text,text,text,uuid)'::regprocedure
            """, admin);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("vinx_identity_owner", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(["search_path=pg_catalog, foundation, pg_temp"], reader.GetFieldValue<string[]>(2));
        for (var index = 3; index < reader.FieldCount; index++) Assert.Equal(index == 3, reader.GetBoolean(index));
    }

    [Theory]
    [InlineData("missing_user", "invalid")]
    [InlineData("missing_identity", "invalid")]
    [InlineData("wrong_user", "conflict")]
    [InlineData("wrong_issuer", "conflict")]
    [InlineData("wrong_old_subject", "conflict")]
    [InlineData("linked_new_subject", "conflict")]
    [InlineData("already_configured", "already_configured")]
    [InlineData("reprovisioned", "reprovisioned")]
    public async Task Reprovisioning_returns_stable_statuses(string scenario, string expected)
    {
        await using var fixture = await StartAsync();
        var seed = await SeedAsync(fixture);
        var authorizationBefore = await AuthorizationProjectionAsync(fixture, seed);
        var request = new Request(seed.UserId, seed.IdentityId, Issuer, OldSubject, NewSubject, Guid.NewGuid());
        switch (scenario)
        {
            case "missing_user": request = request with { UserId = Guid.NewGuid() }; break;
            case "missing_identity": request = request with { IdentityId = Guid.NewGuid() }; break;
            case "wrong_user": request = request with { UserId = await InsertUserAsync(fixture) }; break;
            case "wrong_issuer": request = request with { Issuer = "https://wrong-issuer.test" }; break;
            case "wrong_old_subject": request = request with { OldSubject = "wrong-old-subject" }; break;
            case "linked_new_subject": await InsertIdentityAsync(fixture, await InsertUserAsync(fixture), NewSubject); break;
            case "already_configured": await RotateAsync(fixture, seed, NewSubject, Guid.NewGuid()); break;
        }
        Assert.Equal(expected, await RotateAsync(fixture, request));
        if (scenario == "already_configured")
            Assert.Equal(1, (await ReadAsync(fixture, seed)).ReprovisioningAudits);
        else if (expected != "reprovisioned")
            await AssertUnchangedAsync(fixture, seed, authorizationBefore);
    }

    [Fact]
    public async Task Reprovisioning_rolls_back_when_audit_append_is_denied()
    {
        await using var fixture = await StartAsync();
        var seed = await SeedAsync(fixture);
        await using (var admin = await fixture.OpenAdministratorConnectionAsync())
        await using (var revoke = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_identity_owner", admin))
            await revoke.ExecuteNonQueryAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => RotateAsync(fixture, seed, NewSubject, Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        var state = await ReadAsync(fixture, seed);
        Assert.Equal((OldSubject, null, 0L, 0L), (state.Subject, state.Barrier, state.RevokedSessions, state.ReprovisioningAudits));
    }

    [Fact]
    public async Task Reprovisioning_replay_does_not_duplicate_audit_or_session_transition()
    {
        await using var fixture = await StartAsync();
        var seed = await SeedAsync(fixture);
        Assert.Equal("reprovisioned", await RotateAsync(fixture, seed, NewSubject, Guid.NewGuid()));
        var first = await ReadAsync(fixture, seed);
        Assert.Equal("already_configured", await RotateAsync(fixture, seed, NewSubject, Guid.NewGuid()));
        var replay = await ReadAsync(fixture, seed);
        Assert.Equal((first.Barrier, first.RevokedSessions, first.ReprovisioningAudits),
            (replay.Barrier, replay.RevokedSessions, replay.ReprovisioningAudits));
    }

    [Fact]
    public async Task Reprovisioning_competing_new_subjects_wait_for_the_target_user_lock_then_choose_one_winner()
    {
        await using var fixture = await StartAsync();
        var seed = await SeedAsync(fixture);
        await using var coordinator = await fixture.OpenAdministratorConnectionAsync();
        await using var transaction = await coordinator.BeginTransactionAsync();
        await using (var lockUser = new NpgsqlCommand("SELECT 1 FROM foundation.users WHERE id = @user FOR UPDATE", coordinator, transaction))
        {
            lockUser.Parameters.AddWithValue("user", seed.UserId);
            await lockUser.ExecuteNonQueryAsync();
        }

        var first = RotateAsync(fixture, seed, "new-subject-first", Guid.NewGuid());
        var second = RotateAsync(fixture, seed, "new-subject-second", Guid.NewGuid());
        await WaitForBlockedCallsAsync(fixture, first, second);
        await transaction.CommitAsync();

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result == "reprovisioned");
        Assert.Single(results, result => result == "conflict");
        var state = await ReadAsync(fixture, seed);
        Assert.Contains(state.Subject, new[] { "new-subject-first", "new-subject-second" });
        Assert.Equal(2, state.RevokedSessions);
        Assert.True(state.RevokedForRecovery);
        Assert.Equal(1, state.ReprovisioningAudits);
    }

    [Fact]
    public async Task Reprovisioning_downgrade_fails_closed_after_use()
    {
        await using var fixture = await StartAsync();
        var seed = await SeedAsync(fixture);
        await RotateAsync(fixture, seed, NewSubject, Guid.NewGuid());
        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => migrator.Database.MigrateAsync(BeforeReprovisioning));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(NewSubject, (await ReadAsync(fixture, seed)).Subject);
    }

    private static async Task<FoundationPostgresFixture> StartAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Seed> SeedAsync(FoundationPostgresFixture fixture)
    {
        var userId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users VALUES (@user, 'Recovery', 'recovery@example.test', clock_timestamp(), 1, NULL);
            INSERT INTO foundation.external_identities VALUES (@identity, @user, @issuer, @old, clock_timestamp());
            INSERT INTO foundation.groups VALUES (@group, 'Recovery', @user, @user, @request, clock_timestamp(), clock_timestamp(), NULL, NULL, NULL, 1);
            INSERT INTO foundation.companies VALUES (@company, @group, 'Recovery', @user, @user, @request, clock_timestamp(), clock_timestamp(), NULL, NULL, NULL, 1);
            INSERT INTO foundation.memberships VALUES (@membership, @company, @user, @user, @user, @request, clock_timestamp(), clock_timestamp(), NULL, NULL, NULL, 1);
            INSERT INTO foundation.roles VALUES (@role, @company, 'Recovery', @user, @user, @request, clock_timestamp(), clock_timestamp(), NULL, NULL, NULL, 1);
            INSERT INTO foundation.capabilities VALUES (@capability, 'reprovisioning.graph.assertion', 'Test', clock_timestamp());
            INSERT INTO foundation.role_capabilities VALUES (@role_capability, @company, @role, @capability, 'company', @user, @user, @request, clock_timestamp(), clock_timestamp(), NULL, NULL, NULL, 1);
            INSERT INTO foundation.role_assignments VALUES (@assignment, @company, @role, @membership, @user, @user, @request, clock_timestamp(), clock_timestamp(), NULL, NULL, NULL, 1);
            INSERT INTO foundation.platform_operators (singleton, external_identity_id) VALUES (1, @identity);
            INSERT INTO foundation.user_sessions (id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at)
            VALUES (gen_random_uuid(), @user, @issuer, gen_random_uuid(), '2026-08-23T12:00:00Z', '2026-08-23T13:00:00Z'),
                   (gen_random_uuid(), @user, @issuer, gen_random_uuid(), '2026-08-23T12:01:00Z', '2026-08-23T13:01:00Z');
            """, admin);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("identity", identityId);
        command.Parameters.AddWithValue("issuer", Issuer);
        command.Parameters.AddWithValue("old", OldSubject);
        command.Parameters.AddWithValue("group", ids[0]);
        command.Parameters.AddWithValue("company", ids[1]);
        command.Parameters.AddWithValue("membership", ids[2]);
        command.Parameters.AddWithValue("role", ids[3]);
        command.Parameters.AddWithValue("capability", ids[4]);
        command.Parameters.AddWithValue("role_capability", ids[5]);
        command.Parameters.AddWithValue("assignment", ids[6]);
        command.Parameters.AddWithValue("request", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
        return new Seed(userId, identityId);
    }

    private static Task<string> RotateAsync(FoundationPostgresFixture fixture, Seed seed, string subject, Guid correlationId) =>
        RotateAsync(fixture, new Request(seed.UserId, seed.IdentityId, Issuer, OldSubject, subject, correlationId));

    private static async Task<string> RotateAsync(FoundationPostgresFixture fixture, Request request)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigratorConnectionString);
        await migrator.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM foundation.reprovision_external_identity(@user,@identity,@issuer,@old,@new,@correlation)", migrator);
        command.Parameters.AddWithValue("user", request.UserId);
        command.Parameters.AddWithValue("identity", request.IdentityId);
        command.Parameters.AddWithValue("issuer", request.Issuer);
        command.Parameters.AddWithValue("old", request.OldSubject);
        command.Parameters.AddWithValue("new", request.NewSubject);
        command.Parameters.AddWithValue("correlation", request.CorrelationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<State> ReadAsync(FoundationPostgresFixture fixture, Seed seed)
    {
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT identity_record.user_id, identity_record.id, identity_record.subject, user_record.sessions_valid_after,
                (SELECT count(*) FROM foundation.user_sessions WHERE user_id = @user AND revoked_at IS NOT NULL),
                (SELECT coalesce(bool_and(revoked_by_user_id = @user AND revocation_reason = 'identity_reprovisioned'), false) FROM foundation.user_sessions WHERE user_id = @user),
                (SELECT external_identity_id FROM foundation.platform_operators WHERE singleton = 1),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'identity.external.reprovision'),
                (SELECT actor_user_id FROM audit.audit_events WHERE action = 'identity.external.reprovision'),
                (SELECT resource_id FROM audit.audit_events WHERE action = 'identity.external.reprovision'),
                (SELECT correlation_id FROM audit.audit_events WHERE action = 'identity.external.reprovision'),
                (SELECT metadata_json::text FROM audit.audit_events WHERE action = 'identity.external.reprovision')
            FROM foundation.external_identities AS identity_record JOIN foundation.users AS user_record ON user_record.id = identity_record.user_id
            WHERE identity_record.id = @identity
            """, admin);
        command.Parameters.AddWithValue("user", seed.UserId);
        command.Parameters.AddWithValue("identity", seed.IdentityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new State(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<long>(4), reader.GetBoolean(5), reader.GetGuid(6), reader.GetFieldValue<long>(7), reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetGuid(10), reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private static async Task<string> AuthorizationProjectionAsync(FoundationPostgresFixture fixture, Seed seed)
    {
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT coalesce(string_agg(concat_ws('|', m.id, m.company_id, m.user_id,
                a.id, a.company_id, a.role_id, a.membership_id, r.id, r.company_id, r.name,
                rc.id, rc.company_id, rc.role_id, rc.capability_id, rc.scope,
                c.id, c.name, coalesce(c.description, '')), E'\n'
                ORDER BY m.id, a.id, rc.id, c.id), '')
            FROM foundation.memberships AS m
            JOIN foundation.role_assignments AS a ON a.membership_id = m.id AND a.deleted_at IS NULL
            JOIN foundation.roles AS r ON r.id = a.role_id AND r.deleted_at IS NULL
            JOIN foundation.role_capabilities AS rc ON rc.role_id = a.role_id AND rc.deleted_at IS NULL
            JOIN foundation.capabilities AS c ON c.id = rc.capability_id
            WHERE m.user_id = @user AND m.deleted_at IS NULL
            """, admin);
        command.Parameters.AddWithValue("user", seed.UserId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertUnchangedAsync(FoundationPostgresFixture fixture, Seed seed, string authorizationBefore)
    {
        var state = await ReadAsync(fixture, seed);
        Assert.Equal(seed.UserId, state.UserId);
        Assert.Equal(seed.IdentityId, state.IdentityId);
        Assert.Equal(OldSubject, state.Subject);
        Assert.Null(state.Barrier);
        Assert.Equal(0, state.RevokedSessions);
        Assert.False(state.RevokedForRecovery);
        Assert.Equal(seed.IdentityId, state.OperatorIdentityId);
        Assert.Equal(0, state.ReprovisioningAudits);
        Assert.Equal(authorizationBefore, await AuthorizationProjectionAsync(fixture, seed));
    }

    private static async Task<Guid?> ResolveAsync(FoundationPostgresFixture fixture, string subject)
    {
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT user_id FROM foundation.external_identities WHERE issuer=@issuer AND subject=@subject", admin);
        command.Parameters.AddWithValue("issuer", Issuer);
        command.Parameters.AddWithValue("subject", subject);
        return await command.ExecuteScalarAsync() is Guid userId ? userId : null;
    }

    private static async Task InsertFreshSessionAsync(FoundationPostgresFixture fixture, Guid userId, DateTimeOffset barrier)
    {
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO foundation.user_sessions (id,user_id,issuer,provider_session_id,provider_authenticated_at,token_expires_at) VALUES (gen_random_uuid(),@user,@issuer,gen_random_uuid(),@after,@expires)", runtime);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("issuer", Issuer);
        command.Parameters.AddWithValue("after", barrier.AddMinutes(1));
        command.Parameters.AddWithValue("expires", barrier.AddHours(1));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> InsertUserAsync(FoundationPostgresFixture fixture)
    {
        var userId = Guid.NewGuid();
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand("INSERT INTO foundation.users (id,display_name,email,created_at) VALUES (@id,'Other',@email,clock_timestamp())", admin);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", $"{userId:N}@example.test");
        await command.ExecuteNonQueryAsync();
        return userId;
    }

    private static async Task InsertIdentityAsync(FoundationPostgresFixture fixture, Guid userId, string subject)
    {
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand("INSERT INTO foundation.external_identities VALUES (gen_random_uuid(),@user,@issuer,@subject,clock_timestamp())", admin);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("issuer", Issuer);
        command.Parameters.AddWithValue("subject", subject);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForBlockedCallsAsync(FoundationPostgresFixture fixture, params Task[] calls)
    {
        await using var admin = await fixture.OpenAdministratorConnectionAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var bothBlocked = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%reprovision_external_identity%'", admin);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) == calls.Length) { bothBlocked = true; break; }
            await Task.Delay(20);
        }
        Assert.True(bothBlocked);
        Assert.All(calls, call => Assert.False(call.IsCompleted));
    }

    private sealed record Seed(Guid UserId, Guid IdentityId);
    private sealed record Request(Guid UserId, Guid IdentityId, string Issuer, string OldSubject, string NewSubject, Guid CorrelationId);
    private sealed record State(Guid UserId, Guid IdentityId, string Subject, DateTimeOffset? Barrier, long RevokedSessions, bool RevokedForRecovery,
        Guid OperatorIdentityId, long ReprovisioningAudits, Guid? AuditActorId, Guid? AuditResourceId, Guid? AuditCorrelationId, string? AuditMetadata);
}
