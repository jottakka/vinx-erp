using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipRevocationMigrationTests
{
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
                NOT has_column_privilege('vinx_runtime', 'foundation.memberships', 'user_id', 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        for (var ordinal = 4; ordinal < 14; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }
}
