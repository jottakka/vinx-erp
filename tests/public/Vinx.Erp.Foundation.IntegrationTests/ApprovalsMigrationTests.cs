using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ApprovalsMigrationTests
{
    [Fact]
    public async Task Runtime_can_update_only_approval_lifecycle_columns()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'DELETE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'confirmation_request_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'consumed_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'result_json', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'updated_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'updated_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'request_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'row_version', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'deleted_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'deleted_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'deleted_reason', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'company_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'actor_user_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'action', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'payload_hash', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'policy_version', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'proposal_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'expires_at', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'created_at', 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        for (var ordinal = 4; ordinal < 23; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Fact]
    public async Task Approvals_declare_the_schema_invariants_as_database_constraints()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_expires_after_creation'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_payload_hash_format'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_veto_complete'),
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'foundation'
                      AND tablename = 'approvals'
                      AND indexname = 'ix_approvals_proposal_id'
                      AND indexdef LIKE '%UNIQUE%'),
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'foundation'
                      AND tablename = 'approvals'
                      AND indexname = 'ix_approvals_confirmation_request_id'
                      AND indexdef LIKE '%UNIQUE%'
                      AND indexdef LIKE '%confirmation_request_id IS NOT NULL%')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        for (var ordinal = 0; ordinal < 5; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Fact]
    public async Task Approvals_declare_terminal_constraints_and_the_filtered_live_actor_index()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_consumption_complete'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_terminal_exclusive'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_veto_complete'
                      AND pg_get_constraintdef(oid) LIKE '%approval_expired%'),
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'foundation'
                      AND tablename = 'approvals'
                      AND indexname = 'ix_approvals_company_id_actor_user_id_resource_key'
                      AND regexp_replace(lower(indexdef), '\s+', ' ', 'g')
                          LIKE '%create unique index ix_approvals_company_id_actor_user_id_resource_key%'
                      AND regexp_replace(lower(indexdef), '\s+', ' ', 'g')
                          LIKE '%where ((consumed_at is null) and (deleted_at is null))%')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        for (var ordinal = 0; ordinal < 4; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Fact]
    public async Task Approval_terminal_constraints_reject_partial_consumption_and_consumed_deleted_rows()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (CompanyId, ActorId) = await SeedApprovalScopeAsync(fixture);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        var partialConsumption = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertApprovalAsync(
                administrator,
                CompanyId,
                ActorId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                consumedAt: DateTimeOffset.UtcNow,
                confirmationRequestId: Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.CheckViolation, partialConsumption.SqlState);
        Assert.Equal("ck_approvals_consumption_complete", partialConsumption.ConstraintName);

        var consumedAndDeleted = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertApprovalAsync(
                administrator,
                CompanyId,
                ActorId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                consumedAt: DateTimeOffset.UtcNow,
                confirmationRequestId: Guid.NewGuid(),
                resultJson: "{}",
                deletedAt: DateTimeOffset.UtcNow,
                deletedByUserId: ActorId,
                deletedReason: FoundationApproval.VetoReason));

        Assert.Equal(PostgresErrorCodes.CheckViolation, consumedAndDeleted.SqlState);
        Assert.Equal("ck_approvals_terminal_exclusive", consumedAndDeleted.ConstraintName);
    }

    [Fact]
    public async Task Approval_unique_indexes_reject_live_actor_and_confirmation_request_collisions()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (CompanyId, ActorId) = await SeedApprovalScopeAsync(fixture);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await InsertApprovalAsync(
            administrator,
            CompanyId,
            ActorId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        var liveActorCollision = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertApprovalAsync(
                administrator,
                CompanyId,
                ActorId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, liveActorCollision.SqlState);
        Assert.Equal("ix_approvals_company_id_actor_user_id_resource_key", liveActorCollision.ConstraintName);

        var confirmationRequestId = Guid.NewGuid();
        await InsertApprovalAsync(
            administrator,
            CompanyId,
            ActorId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            consumedAt: DateTimeOffset.UtcNow,
            confirmationRequestId: confirmationRequestId,
            resultJson: "{}");

        var confirmationRequestCollision = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertApprovalAsync(
                administrator,
                CompanyId,
                ActorId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                consumedAt: DateTimeOffset.UtcNow,
                confirmationRequestId: confirmationRequestId,
                resultJson: "{}"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, confirmationRequestCollision.SqlState);
        Assert.Equal("ix_approvals_confirmation_request_id", confirmationRequestCollision.ConstraintName);
    }

    [Fact]
    public async Task Ai_writes_confirm_capability_is_seeded_with_its_stable_identity()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM foundation.capabilities WHERE name = @name", administrator);
        command.Parameters.AddWithValue("name", KnownCapabilities.AiWritesConfirm);

        Assert.Equal(KnownCapabilities.AiWritesConfirmId, await command.ExecuteScalarAsync());
    }

    private static async Task<(Guid CompanyId, Guid ActorId)> SeedApprovalScopeAsync(
        FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Approval group", actorId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Approval company", actorId, requestId);
        var user = new User(actorId, "Approval actor", $"approval-{actorId:N}@example.test");
        administrator.AddRange(group, company, user);
        await administrator.SaveChangesAsync();
        return (company.Id, actorId);
    }

    private static async Task InsertApprovalAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid actorId,
        Guid approvalId,
        Guid proposalId,
        Guid requestId,
        DateTimeOffset? consumedAt = null,
        Guid? confirmationRequestId = null,
        string? resultJson = null,
        DateTimeOffset? deletedAt = null,
        Guid? deletedByUserId = null,
        string? deletedReason = null)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.approvals (
                id, company_id, actor_user_id, action, payload_hash, policy_version,
                proposal_id, confirmation_request_id, expires_at, consumed_at, result_json,
                created_by_user_id, updated_by_user_id, request_id, created_at, updated_at,
                deleted_at, deleted_by_user_id, deleted_reason, row_version)
            VALUES (
                @id, @company_id, @actor_user_id, 'ai.approvals.confirm',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '1.0',
                @proposal_id, @confirmation_request_id, @expires_at, @consumed_at, @result_json,
                @actor_user_id, @actor_user_id, @request_id, @created_at, @created_at,
                @deleted_at, @deleted_by_user_id, @deleted_reason, 1)
            """,
            connection);
        command.Parameters.AddWithValue("id", approvalId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("actor_user_id", actorId);
        command.Parameters.AddWithValue("proposal_id", proposalId);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("created_at", createdAt);
        command.Parameters.AddWithValue(
            "confirmation_request_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)confirmationRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "consumed_at",
            NpgsqlTypes.NpgsqlDbType.TimestampTz,
            (object?)consumedAt ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "result_json",
            NpgsqlTypes.NpgsqlDbType.Jsonb,
            (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "deleted_at",
            NpgsqlTypes.NpgsqlDbType.TimestampTz,
            (object?)deletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "deleted_by_user_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)deletedByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "deleted_reason",
            NpgsqlTypes.NpgsqlDbType.Varchar,
            (object?)deletedReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
