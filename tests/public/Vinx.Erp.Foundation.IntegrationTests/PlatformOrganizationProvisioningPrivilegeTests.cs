using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOrganizationProvisioningPrivilegeTests
{
    [Fact]
    public async Task Provisioning_owner_has_only_insert_privileges_on_mutable_base_tables_and_narrow_technical_policies()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH mutable_tables(table_name) AS (
                VALUES
                    ('groups'::text),
                    ('companies'::text),
                    ('users'::text),
                    ('memberships'::text),
                    ('roles'::text),
                    ('role_capabilities'::text),
                    ('role_assignments'::text)
            )
            SELECT
                table_name,
                has_table_privilege('vinx_provisioning_owner', 'foundation.' || table_name, 'INSERT'),
                NOT has_table_privilege('vinx_provisioning_owner', 'foundation.' || table_name, 'SELECT'),
                NOT has_table_privilege('vinx_provisioning_owner', 'foundation.' || table_name, 'UPDATE'),
                NOT has_table_privilege('vinx_provisioning_owner', 'foundation.' || table_name, 'DELETE')
            FROM mutable_tables
            ORDER BY table_name;

            SELECT
                tablename,
                array_agg(DISTINCT cmd ORDER BY cmd) AS commands
            FROM pg_policies
            WHERE schemaname = 'foundation'
              AND tablename IN ('pending_owner_invitations', 'organization_provisioning_receipts')
            GROUP BY tablename
            ORDER BY tablename;

            SELECT
                has_table_privilege('vinx_provisioning_owner', 'foundation.capabilities', 'SELECT'),
                NOT has_table_privilege('vinx_provisioning_owner', 'foundation.capabilities', 'INSERT'),
                NOT has_table_privilege('vinx_provisioning_owner', 'foundation.capabilities', 'UPDATE'),
                NOT has_table_privilege('vinx_provisioning_owner', 'foundation.capabilities', 'DELETE')
            """,
            administrator);

        await using var reader = await command.ExecuteReaderAsync();
        var mutableTableCount = 0;
        while (await reader.ReadAsync())
        {
            mutableTableCount++;
            Assert.True(reader.GetBoolean(1), reader.GetString(0));
            Assert.True(reader.GetBoolean(2), reader.GetString(0));
            Assert.True(reader.GetBoolean(3), reader.GetString(0));
            Assert.True(reader.GetBoolean(4), reader.GetString(0));
        }

        Assert.Equal(7, mutableTableCount);
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("organization_provisioning_receipts", reader.GetString(0));
        Assert.Equal(["INSERT", "SELECT"], reader.GetFieldValue<string[]>(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pending_owner_invitations", reader.GetString(0));
        Assert.Equal(["INSERT"], reader.GetFieldValue<string[]>(1));
        Assert.False(await reader.ReadAsync());

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }
}
