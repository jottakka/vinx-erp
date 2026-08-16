using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Xunit;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOrganizationProvisioningFunctionTests
{
    [Fact]
    public async Task Provisioning_function_creates_the_complete_owner_graph_and_replays_safely()
    {
        await using var fixture = await StartFixtureAsync();
        await SeedPlatformOperatorAsync(fixture);
        var requestId = Guid.NewGuid();

        var first = await CallAsRuntimeAsync(fixture, requestId, "Northwind Group", "Northwind HQ");
        Assert.Equal("created", first.Status);
        Assert.NotEqual(Guid.Empty, first.GroupId);
        Assert.NotEqual(Guid.Empty, first.CompanyId);
        Assert.NotEqual(Guid.Empty, first.OwnerUserId);

        var state = await ReadGraphStateAsync(fixture);
        Assert.Equal(1, state.Groups);
        Assert.Equal(1, state.Companies);
        Assert.Equal(2, state.Users);
        Assert.Equal(1, state.Memberships);
        Assert.Equal(1, state.OwnerAssignments);
        Assert.Equal(1, state.Invites);
        Assert.Equal(1, state.Receipts);
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal(1, state.ExternalIdentities);
        Assert.Equal(0, state.OperatorMemberships);
        Assert.Equal(4, state.OwnerCapabilityGrants);

        var replay = await CallAsRuntimeAsync(fixture, requestId, "Northwind Group", "Northwind HQ");
        Assert.Equal("replay", replay.Status);
        Assert.Equal(first.GroupId, replay.GroupId);
        Assert.Equal(first.CompanyId, replay.CompanyId);
        Assert.Equal(first.OwnerUserId, replay.OwnerUserId);

        var replayState = await ReadGraphStateAsync(fixture);
        Assert.Equal(state, replayState);
    }

    [Fact]
    public async Task Provisioning_function_rejects_a_different_payload_for_the_same_request_without_partial_state()
    {
        await using var fixture = await StartFixtureAsync();
        await SeedPlatformOperatorAsync(fixture);
        var requestId = Guid.NewGuid();

        await CallAsRuntimeAsync(fixture, requestId, "Northwind Group", "Northwind HQ");
        var before = await ReadGraphStateAsync(fixture);

        var conflict = await CallAsRuntimeAsync(fixture, requestId, "Contoso Group", "Contoso HQ");
        Assert.Equal("idempotency_conflict", conflict.Status);
        Assert.Equal(before, await ReadGraphStateAsync(fixture));
    }

    [Fact]
    public async Task Concurrent_replays_have_one_creation_and_no_duplicate_owner_graph()
    {
        await using var fixture = await StartFixtureAsync();
        await SeedPlatformOperatorAsync(fixture);
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            CallAsRuntimeAsync(fixture, requestId, "Northwind Group", "Northwind HQ")));

        Assert.Equal(1, results.Count(result => result.Status == "created"));
        Assert.Equal(7, results.Count(result => result.Status == "replay"));
        Assert.Single(results.Select(result => result.GroupId).Distinct());
        Assert.Single(results.Select(result => result.CompanyId).Distinct());
        Assert.Single(results.Select(result => result.OwnerUserId).Distinct());
    }

    [Fact]
    public async Task Provisioning_function_rejects_a_non_operator_without_mutating_state()
    {
        await using var fixture = await StartFixtureAsync();
        var before = await ReadGraphStateAsync(fixture);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            CallAsRuntimeAsync(fixture, Guid.NewGuid(), "Northwind Group", "Northwind HQ"));

        Assert.Equal("42501", exception.SqlState);
        Assert.Equal(before, await ReadGraphStateAsync(fixture));
    }

    [Fact]
    public async Task Provisioning_function_rejects_aal1_operator_without_mutating_state()
    {
        await using var fixture = await StartFixtureAsync();
        await SeedPlatformOperatorAsync(fixture);
        var before = await ReadGraphStateAsync(fixture);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            CallAsRuntimeAsync(
                fixture,
                Guid.NewGuid(),
                "Northwind Group",
                "Northwind HQ",
                mfaAssurance: "aal1"));

        Assert.Equal("42501", exception.SqlState);
        Assert.Equal(before, await ReadGraphStateAsync(fixture));
    }

    [Fact]
    public async Task Provisioning_function_ignores_runtime_temp_table_shadows()
    {
        await using var fixture = await StartFixtureAsync();
        await SeedPlatformOperatorAsync(fixture);
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (var shadow = new NpgsqlCommand(
            "CREATE TEMPORARY TABLE organization_provisioning_receipts (id uuid)",
            runtime,
            transaction))
        {
            await shadow.ExecuteNonQueryAsync();
        }

        await using var context = new NpgsqlCommand(
            "SELECT set_config('app.user_id', '00000000-0000-0000-0000-000000000001', true), set_config('app.external_identity_id', '00000000-0000-0000-0000-000000000002', true), set_config('app.mfa_assurance', 'aal2', true)",
            runtime,
            transaction);
        await context.ExecuteNonQueryAsync();

        var requestId = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM foundation.provision_platform_organization(@request_id, @group_name, @company_name, 'Owner', 'owner@example.test', @payload_hash)",
            runtime,
            transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("group_name", "Northwind Group");
        command.Parameters.AddWithValue("company_name", "Northwind HQ");
        command.Parameters.AddWithValue("payload_hash", PayloadHash(requestId, "Northwind Group", "Northwind HQ"));

        Assert.Equal("created", (string?)await command.ExecuteScalarAsync());
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Provisioning_function_is_security_definer_owned_by_a_nonlogin_owner_with_fixed_search_path()
    {
        await using var fixture = await StartFixtureAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                function_row.prosecdef,
                pg_get_userbyid(function_row.proowner),
                function_row.proconfig,
                owner.rolcanlogin,
                owner.rolbypassrls,
                has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege(0::oid, function_row.oid, 'EXECUTE')
            FROM pg_proc AS function_row
            JOIN pg_roles AS owner ON owner.oid = function_row.proowner
            WHERE function_row.oid =
                'foundation.provision_platform_organization(uuid,text,text,text,text,text)'::regprocedure;
            """,
            administrator);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("vinx_provisioning_owner", reader.GetString(1));
        Assert.Equal(["search_path=pg_catalog, foundation, pg_temp"], reader.GetFieldValue<string[]>(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    [Fact]
    public async Task Provisioning_migration_adds_forced_rls_to_provisioning_tables_and_keeps_runtime_privileges_narrow()
    {
        await using var fixture = await StartFixtureAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('foundation.pending_owner_invitations') IS NOT NULL,
                to_regclass('foundation.organization_provisioning_receipts') IS NOT NULL,
                COALESCE((SELECT relforcerowsecurity FROM pg_class WHERE oid = to_regclass('foundation.pending_owner_invitations')), false),
                COALESCE((SELECT relforcerowsecurity FROM pg_class WHERE oid = to_regclass('foundation.organization_provisioning_receipts')), false),
                CASE WHEN to_regclass('foundation.pending_owner_invitations') IS NULL THEN false ELSE NOT (has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'SELECT') OR has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'INSERT') OR has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'UPDATE') OR has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'DELETE')) END,
                CASE WHEN to_regclass('foundation.organization_provisioning_receipts') IS NULL THEN false ELSE NOT (has_table_privilege('vinx_runtime', 'foundation.organization_provisioning_receipts', 'SELECT') OR has_table_privilege('vinx_runtime', 'foundation.organization_provisioning_receipts', 'INSERT') OR has_table_privilege('vinx_runtime', 'foundation.organization_provisioning_receipts', 'UPDATE') OR has_table_privilege('vinx_runtime', 'foundation.organization_provisioning_receipts', 'DELETE')) END,
                has_schema_privilege('vinx_provisioning_owner', 'foundation', 'USAGE'),
                NOT has_schema_privilege('vinx_provisioning_owner', 'foundation', 'CREATE')
            """,
            administrator);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
    }

    private static async Task<ProvisionResult> CallAsRuntimeAsync(
        FoundationPostgresFixture fixture,
        Guid requestId,
        string groupName,
        string companyName,
        string mfaAssurance = "aal2")
    {
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.user_id', '00000000-0000-0000-0000-000000000001', true), set_config('app.external_identity_id', '00000000-0000-0000-0000-000000000002', true), set_config('app.mfa_assurance', @mfa_assurance, true)",
            runtime,
            transaction))
        {
            context.Parameters.AddWithValue("mfa_assurance", mfaAssurance);
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT status, group_id, company_id, owner_user_id, owner_membership_id, owner_invitation_id FROM foundation.provision_platform_organization(@request_id, @group_name, @company_name, @owner_display_name, @owner_email, @payload_hash)",
            runtime,
            transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("group_name", groupName);
        command.Parameters.AddWithValue("company_name", companyName);
        command.Parameters.AddWithValue("owner_display_name", "Owner");
        command.Parameters.AddWithValue("owner_email", "owner@example.test");
        command.Parameters.AddWithValue(
            "payload_hash",
            PayloadHash(requestId, groupName, companyName));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new ProvisionResult(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static string PayloadHash(Guid requestId, string groupName, string companyName)
    {
        var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(
            new[] { requestId.ToString("D"), groupName, companyName, "Owner", "owner@example.test" });
        return Convert.ToHexString(SHA256.HashData(canonicalPayload))
            .ToLowerInvariant();
    }

    private static async Task<GraphState> ReadGraphStateAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.groups),
                (SELECT count(*) FROM foundation.companies),
                (SELECT count(*) FROM foundation.users),
                (SELECT count(*) FROM foundation.memberships),
                (SELECT count(*) FROM foundation.role_assignments),
                (SELECT count(*) FROM foundation.pending_owner_invitations),
                (SELECT count(*) FROM foundation.organization_provisioning_receipts),
                (SELECT count(*) FROM foundation.external_identities),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'platform.organization.provision' AND outcome = 'success'),
                (SELECT count(*) FROM foundation.memberships WHERE user_id = '00000000-0000-0000-0000-000000000001'),
                (SELECT count(*) FROM foundation.role_capabilities WHERE role_id = (SELECT id FROM foundation.roles WHERE name = 'owner'))
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new GraphState(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.GetInt64(9), reader.GetInt64(10));
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task SeedPlatformOperatorAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES ('00000000-0000-0000-0000-000000000001', 'Platform Operator', 'operator@example.test', clock_timestamp());
            INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
            VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 'https://project.supabase.co/auth/v1', 'operator-subject', clock_timestamp());
            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            VALUES (1, '00000000-0000-0000-0000-000000000002');
            """,
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ProvisionResult(string Status, Guid GroupId, Guid CompanyId, Guid OwnerUserId, Guid OwnerMembershipId, Guid OwnerInvitationId);

    private sealed record GraphState(
        long Groups,
        long Companies,
        long Users,
        long Memberships,
        long OwnerAssignments,
        long Invites,
        long Receipts,
        long ExternalIdentities,
        long SuccessAudits,
        long OperatorMemberships,
        long OwnerCapabilityGrants);
}
