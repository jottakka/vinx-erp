using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class OwnerInvitationWorkerIngressFunctionTests
{
    [Fact]
    public async Task Worker_ingress_moves_pending_owner_invitation_to_pending_provider_and_replays_safely()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                "/foundation/platform/organizations",
                new
                {
                    requestId = Guid.NewGuid(),
                    groupName = "Worker ingress group",
                    companyName = "Worker ingress company",
                    ownerDisplayName = "Worker ingress owner",
                    ownerEmail = "worker-ingress@example.test",
                }
            );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var invitationId = Guid.Parse(
            document.RootElement.GetProperty("ownerInvitationId").GetString()!
        );
        var companyId = Guid.Parse(document.RootElement.GetProperty("companyId").GetString()!);
        var correlationId = Guid.NewGuid();

        var first = await AcceptDeliveryAsync(factory.Fixture, invitationId, correlationId);
        Assert.Equal("pending_provider", first.Status);
        Assert.NotNull(first.ProviderPendingAt);
        Assert.True(first.Transitioned);

        var replay = await AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid());
        Assert.Equal("pending_provider", replay.Status);
        Assert.Equal(first.ProviderPendingAt, replay.ProviderPendingAt);
        Assert.False(replay.Transitioned);
        await AssertWorkerIngressAuditAsync(
            factory.Fixture,
            invitationId,
            factory.ActorUserId,
            companyId,
            correlationId
        );
    }

    [Fact]
    public async Task Runtime_and_public_cannot_execute_worker_ingress_or_direct_invitation_dml()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT has_function_privilege('vinx_runtime', 'foundation.accept_owner_invitation_delivery(uuid, uuid)', 'EXECUTE'),
                   has_function_privilege('public', 'foundation.accept_owner_invitation_delivery(uuid, uuid)', 'EXECUTE'),
                   has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'INSERT'),
                   has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'UPDATE'),
                   has_table_privilege('vinx_runtime', 'foundation.pending_owner_invitations', 'DELETE'),
                   has_table_privilege('public', 'foundation.pending_owner_invitations', 'INSERT'),
                   has_table_privilege('public', 'foundation.pending_owner_invitations', 'UPDATE'),
                   has_table_privilege('public', 'foundation.pending_owner_invitations', 'DELETE')
                   ,has_table_privilege('vinx_worker', 'foundation.pending_owner_invitations', 'SELECT')
                   ,has_table_privilege('vinx_worker', 'foundation.pending_owner_invitations', 'UPDATE')
                   ,has_table_privilege('vinx_worker', 'foundation.pending_owner_invitations', 'DELETE')
                   ,has_table_privilege('vinx_worker', 'foundation.organization_provisioning_receipts', 'SELECT')
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.False(reader.GetBoolean(index));
        }
    }

    [Fact]
    public async Task Worker_ingress_database_boundary_uses_the_least_privilege_security_definer_contract()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pg_get_userbyid(ingress_function.proowner),
                ingress_function.prosecdef,
                ingress_function.proconfig,
                has_function_privilege(
                    'vinx_worker',
                    'foundation.accept_owner_invitation_delivery(uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'public',
                    'foundation.accept_owner_invitation_delivery(uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_runtime',
                    'foundation.accept_owner_invitation_delivery(uuid,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_migrator',
                    'foundation.accept_owner_invitation_delivery(uuid,uuid)',
                    'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        ingress_function.proacl,
                        acldefault('f', ingress_function.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee NOT IN (
                          ingress_function.proowner,
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_worker')))
            FROM pg_proc AS ingress_function
            WHERE ingress_function.oid =
                'foundation.accept_owner_invitation_delivery(uuid,uuid)'::regprocedure;

            SELECT
                worker.rolcanlogin,
                worker.rolinherit,
                worker.rolbypassrls,
                worker.rolsuper,
                worker.rolcreatedb,
                worker.rolcreaterole,
                worker_owner.rolcanlogin,
                worker_owner.rolinherit,
                worker_owner.rolbypassrls,
                worker_owner.rolsuper,
                worker_owner.rolcreatedb,
                worker_owner.rolcreaterole,
                has_schema_privilege('vinx_worker', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_worker', 'foundation', 'CREATE'),
                has_schema_privilege('vinx_worker_owner', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_worker_owner', 'foundation', 'CREATE')
            FROM pg_roles AS worker
            CROSS JOIN pg_roles AS worker_owner
            WHERE worker.rolname = 'vinx_worker'
              AND worker_owner.rolname = 'vinx_worker_owner';

            SELECT
                EXISTS (
                    SELECT 1
                    FROM pg_auth_members AS membership
                    INNER JOIN pg_roles AS role ON role.oid = membership.roleid
                    INNER JOIN pg_roles AS member ON member.oid = membership.member
                    WHERE role.rolname = 'vinx_worker_owner'
                      AND member.rolname = 'vinx_migrator'
                      AND NOT membership.inherit_option
                      AND membership.set_option),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_auth_members AS membership
                    INNER JOIN pg_roles AS role ON role.oid = membership.roleid
                    INNER JOIN pg_roles AS member ON member.oid = membership.member
                    WHERE role.rolname = 'vinx_worker_owner'
                      AND member.rolname <> 'vinx_migrator');

            SELECT relation.relname, relation.relrowsecurity, relation.relforcerowsecurity
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema_row ON schema_row.oid = relation.relnamespace
            WHERE schema_row.nspname = 'foundation'
              AND relation.relname IN (
                  'pending_owner_invitations',
                  'organization_provisioning_receipts')
            ORDER BY relation.relname;

            SELECT tablename, policyname, cmd, array_to_string(roles, ',')
            FROM pg_policies
            WHERE schemaname = 'foundation'
              AND roles @> ARRAY['vinx_worker_owner']::name[]
            ORDER BY tablename, policyname;
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("vinx_worker_owner", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.False(reader.GetBoolean(9));
        Assert.False(reader.GetBoolean(10));
        Assert.False(reader.GetBoolean(11));
        Assert.True(reader.GetBoolean(12));
        Assert.False(reader.GetBoolean(13));
        Assert.True(reader.GetBoolean(14));
        Assert.False(reader.GetBoolean(15));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("organization_provisioning_receipts", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pending_owner_invitations", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(await reader.ReadAsync());

        Assert.True(await reader.NextResultAsync());
        var policies = new List<(string Table, string Name, string Command, string Roles)>();
        while (await reader.ReadAsync())
        {
            policies.Add(
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            );
        }

        Assert.Equal(
            [
                // O conjunto lista também as políticas do fluxo paralelo de convite
                // de membro: a consulta enumera exaustivamente as políticas do
                // vinx_worker_owner no schema, e ambos os fluxos compartilham o papel.
                (
                    "memberships",
                    "memberships_worker_owner_read",
                    "SELECT",
                    "vinx_worker_owner"
                ),
                (
                    "organization_provisioning_receipts",
                    "organization_provisioning_receipts_worker_owner_read",
                    "SELECT",
                    "vinx_worker_owner"
                ),
                (
                    "pending_member_invitations",
                    "pending_member_invitations_worker_owner_read",
                    "SELECT",
                    "vinx_worker_owner"
                ),
                (
                    "pending_member_invitations",
                    "pending_member_invitations_worker_owner_update",
                    "UPDATE",
                    "vinx_worker_owner"
                ),
                (
                    "pending_owner_invitations",
                    "pending_owner_invitations_worker_owner_read",
                    "SELECT",
                    "vinx_worker_owner"
                ),
                (
                    "pending_owner_invitations",
                    "pending_owner_invitations_worker_owner_update",
                    "UPDATE",
                    "vinx_worker_owner"
                ),
            ],
            policies
        );
    }

    [Fact]
    public async Task Worker_ingress_fails_closed_when_provisioning_receipt_is_missing()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var companyId = await ProvisionCompanyAsync(factory);
        var invitationId = await SeedInvitationWithoutReceiptAsync(factory.Fixture, companyId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid())
        );

        Assert.Equal("23514", exception.SqlState);
        await AssertInvitationStateAsync(factory.Fixture, invitationId, "pending_delivery", null);
        Assert.Equal(0, await CountAuditsForInvitationAsync(factory.Fixture, invitationId));
    }

    [Fact]
    public async Task Worker_ingress_fails_closed_when_receipt_owner_does_not_match_invitation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var invitationId = await ProvisionInvitationAsync(factory);
        await SetReceiptOwnerDifferentFromInvitationAsync(factory.Fixture, invitationId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid())
        );

        Assert.Equal("23514", exception.SqlState);
        await AssertInvitationStateAsync(factory.Fixture, invitationId, "pending_delivery", null);
        Assert.Equal(0, await CountAuditsForInvitationAsync(factory.Fixture, invitationId));
    }

    [Fact]
    public async Task Worker_ingress_rolls_back_state_when_audit_permission_is_missing()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var invitationId = await ProvisionInvitationAsync(factory);

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        await using (
            var revoke = new NpgsqlCommand(
                "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_worker_owner",
                administrator
            )
        )
        {
            await revoke.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<PostgresException>(() =>
            AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid())
        );

        await AssertInvitationStateAsync(factory.Fixture, invitationId, "pending_delivery", null);
        Assert.Equal(0, await CountAuditsForInvitationAsync(factory.Fixture, invitationId));
    }

    [Fact]
    public async Task Coordinated_duplicate_worker_invocations_transition_once_and_audit_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var invitationId = await ProvisionInvitationAsync(factory);

        var results = await Task.WhenAll(
            AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid()),
            AcceptDeliveryAsync(factory.Fixture, invitationId, Guid.NewGuid())
        );

        Assert.Single(results, result => result.Transitioned);
        Assert.Single(results, result => !result.Transitioned);
        Assert.All(results, result => Assert.Equal("pending_provider", result.Status));
        Assert.All(results, result => Assert.NotNull(result.ProviderPendingAt));
        Assert.Equal(results[0].ProviderPendingAt, results[1].ProviderPendingAt);
        Assert.Equal(1, await CountAuditsForInvitationAsync(factory.Fixture, invitationId));
    }

    [Fact]
    public async Task Provisioning_receipt_cannot_be_created_twice_for_one_invitation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var invitationId = await ProvisionInvitationAsync(factory);

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var duplicate = new NpgsqlCommand(
            """
            INSERT INTO foundation.organization_provisioning_receipts (
                id, actor_user_id, request_id, payload_hash, group_id, company_id,
                owner_user_id, owner_membership_id, owner_invitation_id)
            SELECT gen_random_uuid(), actor_user_id, gen_random_uuid(), payload_hash,
                group_id, company_id, owner_user_id, owner_membership_id, owner_invitation_id
            FROM foundation.organization_provisioning_receipts
            WHERE owner_invitation_id = @invitation_id
            """,
            administrator
        );
        duplicate.Parameters.AddWithValue("invitation_id", invitationId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            duplicate.ExecuteNonQueryAsync()
        );

        Assert.Equal("23505", exception.SqlState);
    }

    private static async Task<DeliveryState> AcceptDeliveryAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid correlationId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, provider_pending_at, transitioned
            FROM foundation.accept_owner_invitation_delivery(@invitation_id, @correlation_id)
            """,
            worker
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("correlation_id", correlationId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DeliveryState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetBoolean(2)
        );
    }

    private static async Task<Guid> ProvisionInvitationAsync(FoundationApiFactory factory)
    {
        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                "/foundation/platform/organizations",
                new
                {
                    requestId = Guid.NewGuid(),
                    groupName = $"Worker ingress group {Guid.NewGuid():N}",
                    companyName = $"Worker ingress company {Guid.NewGuid():N}",
                    ownerDisplayName = "Worker ingress owner",
                    ownerEmail = $"worker-ingress-{Guid.NewGuid():N}@example.test",
                }
            );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("ownerInvitationId").GetString()!);
    }

    private static async Task<Guid> ProvisionCompanyAsync(FoundationApiFactory factory)
    {
        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                "/foundation/platform/organizations",
                new
                {
                    requestId = Guid.NewGuid(),
                    groupName = $"Worker ingress group {Guid.NewGuid():N}",
                    companyName = $"Worker ingress company {Guid.NewGuid():N}",
                    ownerDisplayName = "Worker ingress owner",
                    ownerEmail = $"worker-ingress-{Guid.NewGuid():N}@example.test",
                }
            );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("companyId").GetString()!);
    }

    private static async Task<Guid> SeedInvitationWithoutReceiptAsync(
        FoundationPostgresFixture fixture,
        Guid companyId
    )
    {
        var userId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'orphan worker invitation', @email, clock_timestamp());
            INSERT INTO foundation.pending_owner_invitations (id, user_id, company_id, status)
            VALUES (@invitation_id, @user_id, @company_id, 'pending_delivery');
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("email", $"orphan-{userId:N}@example.test");
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync();
        return invitationId;
    }

    private static async Task SetReceiptOwnerDifferentFromInvitationAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        var differentOwnerUserId = Guid.NewGuid();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'different receipt owner', @email, clock_timestamp());
            UPDATE foundation.organization_provisioning_receipts
            SET owner_user_id = @user_id
            WHERE owner_invitation_id = @invitation_id;
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", differentOwnerUserId);
        command.Parameters.AddWithValue(
            "email",
            $"different-owner-{differentOwnerUserId:N}@example.test"
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertInvitationStateAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        string expectedStatus,
        DateTimeOffset? expectedProviderPendingAt
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, provider_pending_at FROM foundation.pending_owner_invitations WHERE id = @invitation_id",
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedStatus, reader.GetString(0));
        Assert.Equal(
            expectedProviderPendingAt,
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)
        );
    }

    private static async Task<int> CountAuditsForInvitationAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM audit.audit_events
            WHERE resource_id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertWorkerIngressAuditAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid expectedActorUserId,
        Guid expectedCompanyId,
        Guid expectedCorrelationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT source, action, scope, requested_company_id, actor_user_id,
                resource_type, resource_id, outcome, reason, metadata_json::text, correlation_id
            FROM audit.audit_events
            WHERE resource_id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("worker", reader.GetString(0));
        Assert.Equal("owner_invitation.delivery.ingress", reader.GetString(1));
        Assert.Equal("company", reader.GetString(2));
        Assert.Equal(expectedCompanyId, reader.GetGuid(3));
        Assert.Equal(expectedActorUserId, reader.GetGuid(4));
        Assert.Equal("owner_invitation", reader.GetString(5));
        Assert.Equal(invitationId, reader.GetGuid(6));
        Assert.Equal("success", reader.GetString(7));
        Assert.True(reader.IsDBNull(8));
        Assert.Equal("{}", reader.GetString(9));
        Assert.Equal(expectedCorrelationId, reader.GetGuid(10));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task SeedPlatformOperatorAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            SELECT 1, identity_record.id
            FROM foundation.external_identities AS identity_record
            WHERE identity_record.user_id = @user_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record DeliveryState(
        string Status,
        DateTimeOffset? ProviderPendingAt,
        bool Transitioned
    );
}
