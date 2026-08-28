using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftSchemaTests
{
    [Fact]
    public async Task Runtime_rls_fails_closed_without_context_and_hides_another_requester()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var companyId = Guid.NewGuid();
        var secondCompanyId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var otherRequesterId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var setup = new NpgsqlCommand(
                """
                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (@requester_id, 'Requester', 'requester@example.test', clock_timestamp()),
                    (@other_requester_id, 'Other requester', 'other-requester@example.test', clock_timestamp());
                INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
                VALUES (@group_id, 'Group', @requester_id, @requester_id, @request_id, clock_timestamp(), clock_timestamp(), 1);
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
                VALUES (@company_id, @group_id, 'Company', @requester_id, @requester_id, @request_id, clock_timestamp(), clock_timestamp(), 1);
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
                VALUES (@second_company_id, @group_id, 'Second company', @requester_id, @requester_id, @request_id, clock_timestamp(), clock_timestamp(), 1);
                INSERT INTO paguai.requests (id, company_id, requester_user_id, request_id, title, description, amount, currency, due_date, beneficiary_display_name, proof_requirement)
                VALUES (@draft_id, @company_id, @requester_id, @request_id, 'Office supplies', 'Paper', 1250.00, 'BRL', DATE '2026-09-01', 'Fornecedor Demo', 'required');
                INSERT INTO paguai.requests (id, company_id, requester_user_id, request_id, title, description, amount, currency, due_date, beneficiary_display_name, proof_requirement)
                VALUES (@second_draft_id, @second_company_id, @requester_id, @second_request_id, 'Second office supplies', 'Second paper', 90.00, 'BRL', DATE '2026-09-02', 'Beneficiário Teste', 'optional');
                """,
                administrator
            )
        )
        {
            setup.Parameters.AddWithValue("company_id", companyId);
            setup.Parameters.AddWithValue("second_company_id", secondCompanyId);
            setup.Parameters.AddWithValue("group_id", Guid.NewGuid());
            setup.Parameters.AddWithValue("requester_id", requesterId);
            setup.Parameters.AddWithValue("other_requester_id", otherRequesterId);
            setup.Parameters.AddWithValue("request_id", requestId);
            setup.Parameters.AddWithValue("draft_id", Guid.NewGuid());
            setup.Parameters.AddWithValue("second_request_id", Guid.NewGuid());
            setup.Parameters.AddWithValue("second_draft_id", Guid.NewGuid());
            await setup.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        Assert.Equal(0L, await CountRequestsAsync(runtime));

        await using var transaction = await runtime.BeginTransactionAsync();
        await SetContextAsync(runtime, transaction, companyId, otherRequesterId);
        Assert.Equal(0L, await CountRequestsAsync(runtime, transaction));
        await SetContextAsync(runtime, transaction, companyId, requesterId);
        Assert.Equal(1L, await CountRequestsAsync(runtime, transaction));
        Assert.Equal([companyId], await ReadRequestCompanyIdsAsync(runtime, transaction));
        await SetContextAsync(runtime, transaction, secondCompanyId, requesterId);
        Assert.Equal(1L, await CountRequestsAsync(runtime, transaction));
        Assert.Equal([secondCompanyId], await ReadRequestCompanyIdsAsync(runtime, transaction));
        await transaction.RollbackAsync();
    }

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

    [Fact]
    public async Task Invoice_file_schema_uses_a_nullable_same_company_restrict_foreign_key()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                column_info.is_nullable,
                column_info.data_type,
                referential_info.delete_rule,
                array_agg(source_column.attname ORDER BY source_key.ordinality),
                array_agg(target_column.attname ORDER BY target_key.ordinality),
                target_schema.nspname,
                target_table.relname
            FROM information_schema.columns AS column_info
            INNER JOIN information_schema.table_constraints AS constraint_info
                ON constraint_info.constraint_schema = column_info.table_schema
                AND constraint_info.table_name = column_info.table_name
                AND constraint_info.constraint_type = 'FOREIGN KEY'
            INNER JOIN information_schema.referential_constraints AS referential_info
                ON referential_info.constraint_schema = constraint_info.constraint_schema
                AND referential_info.constraint_name = constraint_info.constraint_name
            INNER JOIN pg_constraint AS constraint_catalog
                ON constraint_catalog.conname = constraint_info.constraint_name
            INNER JOIN pg_class AS target_table ON target_table.oid = constraint_catalog.confrelid
            INNER JOIN pg_namespace AS target_schema ON target_schema.oid = target_table.relnamespace
            INNER JOIN unnest(constraint_catalog.conkey) WITH ORDINALITY AS source_key(attnum, ordinality)
                ON true
            INNER JOIN pg_attribute AS source_column
                ON source_column.attrelid = constraint_catalog.conrelid AND source_column.attnum = source_key.attnum
            INNER JOIN unnest(constraint_catalog.confkey) WITH ORDINALITY AS target_key(attnum, ordinality)
                ON target_key.ordinality = source_key.ordinality
            INNER JOIN pg_attribute AS target_column
                ON target_column.attrelid = constraint_catalog.confrelid AND target_column.attnum = target_key.attnum
            WHERE column_info.table_schema = 'paguai'
                AND column_info.table_name = 'requests'
                AND column_info.column_name = 'invoice_file_id'
                AND constraint_catalog.conrelid = 'paguai.requests'::regclass
            GROUP BY
                column_info.is_nullable,
                column_info.data_type,
                referential_info.delete_rule,
                target_schema.nspname,
                target_table.relname,
                constraint_catalog.oid
            HAVING array_agg(source_column.attname ORDER BY source_key.ordinality)
                = ARRAY['company_id', 'invoice_file_id']::name[]
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("YES", reader.GetString(0));
        Assert.Equal("uuid", reader.GetString(1));
        Assert.Equal("RESTRICT", reader.GetString(2));
        Assert.Equal(new[] { "company_id", "invoice_file_id" }, reader.GetFieldValue<string[]>(3));
        Assert.Equal(new[] { "company_id", "id" }, reader.GetFieldValue<string[]>(4));
        Assert.Equal("foundation", reader.GetString(5));
        Assert.Equal("files", reader.GetString(6));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task SetContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid userId
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.company_id', @company_id, true), set_config('app.user_id', @user_id, true)",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("company_id", companyId.ToString());
        command.Parameters.AddWithValue("user_id", userId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRequestsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM paguai.requests",
            connection,
            transaction
        );
        return (long)(
            await command.ExecuteScalarAsync()
            ?? throw new Xunit.Sdk.XunitException("Paguai count returned no value.")
        );
    }

    private static async Task<IReadOnlyList<Guid>> ReadRequestCompanyIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT company_id FROM paguai.requests ORDER BY company_id",
            connection,
            transaction
        );
        await using var reader = await command.ExecuteReaderAsync();
        var companyIds = new List<Guid>();
        while (await reader.ReadAsync())
            companyIds.Add(reader.GetGuid(0));
        return companyIds;
    }
}
