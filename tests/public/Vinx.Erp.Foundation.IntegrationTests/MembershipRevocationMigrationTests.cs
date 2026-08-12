using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipRevocationMigrationTests
{
    private const string BeforeRuntimeMembershipRevocation = "20260812040429_AddCurrentActorMembershipDiscovery";

    [Fact]
    public async Task Runtime_can_update_only_membership_revocation_lifecycle_columns()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_runtime', 'foundation.memberships', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.memberships', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.memberships', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.memberships', 'DELETE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'deleted_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'deleted_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'deleted_reason', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'updated_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'updated_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'request_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.memberships', 'row_version', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.memberships', 'id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.memberships', 'company_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.memberships', 'user_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.memberships', 'created_by_user_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.memberships', 'created_at', 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        for (var ordinal = 4; ordinal < 16; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Fact]
    public async Task Downgrade_removes_runtime_membership_revocation_update_columns()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeRuntimeMembershipRevocation);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                NOT has_table_privilege('vinx_runtime', 'foundation.memberships', 'UPDATE'),
                NOT has_any_column_privilege('vinx_runtime', 'foundation.memberships', 'UPDATE'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_attribute AS attribute
                    CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                    WHERE attribute.attrelid = 'foundation.memberships'::regclass
                      AND NOT attribute.attisdropped
                      AND privilege.grantee = 'vinx_runtime'::regrole
                      AND privilege.privilege_type = 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task Runtime_membership_revocation_update_remains_company_rls_scoped()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actorId = Guid.NewGuid();
        var allowedCompanyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var allowedMembershipId = Guid.NewGuid();
        var otherMembershipId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, 'Grupo', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@actor_id, 'Actor', 'membership-revocation@example.test', clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES
                (@allowed_company_id, @group_id, 'Permitida', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp()),
                (@other_company_id, @group_id, 'Outra', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES
                (@allowed_membership_id, @allowed_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp()),
                (@other_membership_id, @other_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            """, administrator))
        {
            seed.Parameters.AddWithValue("group_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("actor_id", actorId);
            seed.Parameters.AddWithValue("request_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("allowed_company_id", allowedCompanyId);
            seed.Parameters.AddWithValue("other_company_id", otherCompanyId);
            seed.Parameters.AddWithValue("allowed_membership_id", allowedMembershipId);
            seed.Parameters.AddWithValue("other_membership_id", otherMembershipId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (var setCompany = new NpgsqlCommand(
            "SELECT set_config('app.company_id', @company_id, true)", runtime, transaction))
        {
            setCompany.Parameters.AddWithValue("company_id", allowedCompanyId.ToString());
            await setCompany.ExecuteNonQueryAsync();
        }

        await using var revoke = new NpgsqlCommand(
            """
            UPDATE foundation.memberships
            SET deleted_at = clock_timestamp(),
                deleted_by_user_id = @actor_id,
                deleted_reason = 'membership_revoked',
                updated_by_user_id = @actor_id,
                updated_at = clock_timestamp(),
                request_id = @request_id,
                row_version = row_version + 1
            WHERE id = @membership_id
            """, runtime, transaction);
        revoke.Parameters.AddWithValue("actor_id", actorId);
        revoke.Parameters.AddWithValue("request_id", Guid.NewGuid());
        revoke.Parameters.AddWithValue("membership_id", allowedMembershipId);
        Assert.Equal(1, await revoke.ExecuteNonQueryAsync());

        revoke.Parameters["request_id"].Value = Guid.NewGuid();
        revoke.Parameters["membership_id"].Value = otherMembershipId;
        Assert.Equal(0, await revoke.ExecuteNonQueryAsync());
    }
}
