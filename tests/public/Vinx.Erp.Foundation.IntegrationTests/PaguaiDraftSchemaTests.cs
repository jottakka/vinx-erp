using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftSchemaTests
{
    [Fact]
    public async Task Draft_metadata_schema_forces_requester_rls_and_grants_runtime_only_read_and_insert()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                relation.relrowsecurity,
                relation.relforcerowsecurity,
                has_schema_privilege('vinx_runtime', 'paguai', 'USAGE'),
                has_schema_privilege('vinx_runtime', 'paguai', 'CREATE'),
                has_table_privilege('vinx_runtime', 'paguai.requests', 'SELECT'),
                has_table_privilege('vinx_runtime', 'paguai.requests', 'INSERT'),
                has_table_privilege('vinx_runtime', 'paguai.requests', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'paguai.requests', 'DELETE'),
                has_table_privilege('public', 'paguai.requests', 'SELECT'),
                policy.qual,
                policy.with_check
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            INNER JOIN pg_policies AS policy
                ON policy.schemaname = schema.nspname
                AND policy.tablename = relation.relname
                AND policy.policyname = 'paguai_requests_requester_isolation'
            WHERE schema.nspname = 'paguai' AND relation.relname = 'requests'
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.Contains("current_setting('app.company_id'::text, true)", reader.GetString(9));
        Assert.Contains("current_setting('app.user_id'::text, true)", reader.GetString(9));
        Assert.Contains("NULLIF", reader.GetString(10));
    }
}
