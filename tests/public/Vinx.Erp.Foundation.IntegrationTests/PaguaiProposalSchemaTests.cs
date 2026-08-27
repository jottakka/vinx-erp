using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiProposalSchemaTests
{
    [Fact]
    public async Task Proposal_schema_forces_actor_company_rls_and_keeps_only_safe_columns()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT relation.relrowsecurity, relation.relforcerowsecurity,
                has_table_privilege('vinx_runtime', 'paguai.draft_proposals', 'SELECT'),
                has_table_privilege('vinx_runtime', 'paguai.draft_proposals', 'INSERT'),
                has_table_privilege('vinx_runtime', 'paguai.draft_proposals', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'paguai.draft_proposals', 'DELETE'),
                policy.qual, policy.with_check,
                array_agg(column_info.column_name ORDER BY column_info.column_name)
            FROM pg_class relation
            JOIN pg_namespace schema_info ON schema_info.oid = relation.relnamespace
            JOIN pg_policies policy ON policy.schemaname = schema_info.nspname
                AND policy.tablename = relation.relname
                AND policy.policyname = 'paguai_draft_proposals_actor_company_isolation'
            JOIN information_schema.columns column_info ON column_info.table_schema = schema_info.nspname
                AND column_info.table_name = relation.relname
            WHERE schema_info.nspname = 'paguai' AND relation.relname = 'draft_proposals'
            GROUP BY relation.relrowsecurity, relation.relforcerowsecurity, policy.qual, policy.with_check
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.Contains("app.company_id", reader.GetString(6));
        Assert.Contains("app.user_id", reader.GetString(6));
        Assert.Contains("app.company_id", reader.GetString(7));
        Assert.Contains("app.user_id", reader.GetString(7));
        var columns = reader.GetFieldValue<string[]>(8);
        Assert.DoesNotContain(columns, name => name.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("ocr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("bytes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Proposal_schema_has_one_live_request_and_one_draft_per_proposal()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('paguai.uq_paguai_draft_proposals_live_request') IS NOT NULL,
                to_regclass('paguai.uq_paguai_requests_source_proposal_id') IS NOT NULL,
                pg_get_indexdef('paguai.uq_paguai_draft_proposals_live_request'::regclass),
                pg_get_indexdef('paguai.uq_paguai_requests_source_proposal_id'::regclass)
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Contains("state", reader.GetString(2), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending", reader.GetString(2), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source_proposal_id", reader.GetString(3));
    }

    [Fact]
    public async Task Proposal_payload_columns_are_not_updatable_by_runtime_or_worker()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        var lifecycleColumns = new[]
        {
            "state",
            "result_draft_id",
            "resolution_request_id",
            "resolved_at",
            "row_version",
        };
        var immutableColumns = new[]
        {
            "id",
            "company_id",
            "actor_user_id",
            "request_id",
            "file_record_id",
            "title",
            "description",
            "amount",
            "currency",
            "due_date",
            "beneficiary_display_name",
            "proof_requirement",
            "confidence",
            "source_references",
            "policy_version",
            "payload_hash",
            "created_at",
            "expires_at",
        };

        foreach (var role in new[] { "vinx_runtime", "vinx_worker" })
        {
            foreach (var column in lifecycleColumns)
            {
                Assert.True(await HasUpdatePrivilegeAsync(administrator, role, column));
            }

            foreach (var column in immutableColumns)
            {
                Assert.False(await HasUpdatePrivilegeAsync(administrator, role, column));
            }
        }
    }

    private static async Task<bool> HasUpdatePrivilegeAsync(
        NpgsqlConnection connection,
        string role,
        string column)
    {
        await using var command = new NpgsqlCommand(
            "SELECT has_column_privilege(@role, 'paguai.draft_proposals', @column, 'UPDATE')",
            connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
