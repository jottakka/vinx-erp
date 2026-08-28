using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>RED contract for the security boundary of the employee directory function.</summary>
public sealed class EmployeeDirectoryFunctionSecurityTests
{
    private const string FunctionSignature =
        "foundation.create_employee_directory_entry(uuid,uuid,text,text,text)";

    [Fact]
    public async Task Function_owner_is_non_login_and_runtime_cannot_write_the_graph_directly()
    {
        await using var fixture = await StartFixtureAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                function_row.prosecdef,
                pg_get_userbyid(function_row.proowner),
                function_row.proconfig,
                owner.rolcanlogin,
                owner.rolinherit,
                owner.rolbypassrls,
                has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                has_table_privilege('vinx_runtime', 'foundation.users', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.memberships', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.employees', 'INSERT')
            FROM pg_proc AS function_row
            JOIN pg_roles AS owner ON owner.oid = function_row.proowner
            WHERE function_row.oid = '{FunctionSignature}'::regprocedure;
            """,
            administrator
        );

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("vinx_employee_directory_owner", reader.GetString(1));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.False(reader.GetBoolean(9));
    }

    [Fact]
    public async Task Function_rechecks_company_membership_and_capability_before_creating_an_employee()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedAuthorizedCompanyAsync(fixture);
        var unauthorizedCompanyId = Guid.NewGuid();

        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            var groupId = Guid.NewGuid();
            var otherUserId = Guid.NewGuid();
            var otherMembershipId = Guid.NewGuid();
            var otherRequestId = Guid.NewGuid();
            administrator.Add(
                new Group(groupId, $"Other group {groupId:N}", scenario.ActorUserId, Guid.NewGuid())
            );
            administrator.Add(
                new Company(
                    unauthorizedCompanyId,
                    groupId,
                    $"Other company {unauthorizedCompanyId:N}",
                    scenario.ActorUserId,
                    Guid.NewGuid()
                )
            );
            var otherUser = new User(
                otherUserId,
                "Protected employee",
                $"protected-{otherUserId:N}@example.test"
            );
            var otherMembership = new Membership(
                otherMembershipId,
                unauthorizedCompanyId,
                otherUser.Id,
                scenario.ActorUserId,
                otherRequestId
            );
            administrator.AddRange(
                otherUser,
                otherMembership,
                new Employee(
                    Guid.NewGuid(),
                    unauthorizedCompanyId,
                    otherMembership.Id,
                    "Protected employee",
                    scenario.ActorUserId,
                    otherRequestId
                )
            );
            await administrator.SaveChangesAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (
            var context = new NpgsqlCommand(
                "SELECT set_config('app.user_id', @user_id, true)",
                runtime,
                transaction
            )
        )
        {
            context.Parameters.AddWithValue("user_id", scenario.ActorUserId.ToString());
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT status FROM foundation.create_employee_directory_entry(@request_id, @company_id, 'Employee', 'employee@example.test', @payload_hash)",
            runtime,
            transaction
        );
        command.Parameters.AddWithValue("request_id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", unauthorizedCompanyId);
        command.Parameters.AddWithValue("payload_hash", new string('a', 64));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteScalarAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await transaction.RollbackAsync();

        await using var verify = fixture.CreateAdministratorDbContext();
        Assert.Empty(
            await verify
                .Employees.Where(item =>
                    item.CompanyId == unauthorizedCompanyId && item.DisplayName == "Employee"
                )
                .ToListAsync()
        );

        await using var listRuntime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await listRuntime.OpenAsync();
        await using var listTransaction = await listRuntime.BeginTransactionAsync();
        await using (
            var context = new NpgsqlCommand(
                """
                SELECT
                    set_config('app.user_id', @user_id, true),
                    set_config('app.authorized_company_ids', @authorized_company_ids, true)
                """,
                listRuntime,
                listTransaction
            )
        )
        {
            context.Parameters.AddWithValue("user_id", scenario.ActorUserId.ToString());
            context.Parameters.AddWithValue(
                "authorized_company_ids",
                scenario.CompanyId.ToString()
            );
            await context.ExecuteNonQueryAsync();
        }
        await using var list = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.employees WHERE company_id = @company_id",
            listRuntime,
            listTransaction
        );
        list.Parameters.AddWithValue("company_id", unauthorizedCompanyId);
        Assert.Equal(0L, (long)(await list.ExecuteScalarAsync())!);
        await listTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Platform_owner_capability_function_requires_an_exact_provisioning_receipt_before_granting_employee_create()
    {
        await using var fixture = await StartFixtureAsync();
        var owner = await SeedAuthorizedCompanyAsync(
            fixture,
            grantEmployeeCreate: false,
            grantRolesAssign: true
        );
        var platformOperator = await SeedPlatformOperatorAsync(fixture);
        var requestId = Guid.NewGuid();
        await SeedProvisioningReceiptWithDifferentRequestAsync(
            fixture,
            platformOperator.UserId,
            owner,
            Guid.NewGuid()
        );

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (
            var context = new NpgsqlCommand(
                """
                SELECT
                    set_config('app.user_id', @user_id, true),
                    set_config('app.external_identity_id', @external_identity_id, true),
                    set_config('app.mfa_assurance', 'aal2', true)
                """,
                runtime,
                transaction
            )
        )
        {
            context.Parameters.AddWithValue("user_id", platformOperator.UserId.ToString());
            context.Parameters.AddWithValue(
                "external_identity_id",
                platformOperator.ExternalIdentityId.ToString()
            );
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT foundation.ensure_platform_owner_employee_create(@request_id, @company_id, @owner_membership_id)",
            runtime,
            transaction
        );
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("company_id", owner.CompanyId);
        command.Parameters.AddWithValue("owner_membership_id", owner.MembershipId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteScalarAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await transaction.RollbackAsync();

        await using var verify = fixture.CreateAdministratorDbContext();
        var employeeCreate = await verify.Capabilities.SingleAsync(item =>
            item.Name == "foundation.employees.create"
        );
        Assert.Empty(
            await verify
                .RoleCapabilities.Where(item =>
                    item.RoleId == owner.RoleId
                    && item.CapabilityId == employeeCreate.Id
                    && item.DeletedAt == null
                )
                .ToListAsync()
        );
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Scenario> SeedAuthorizedCompanyAsync(
        FoundationPostgresFixture fixture,
        bool grantEmployeeCreate = true,
        bool grantRolesAssign = false
    )
    {
        var actorUserId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Add(new User(actorUserId, "Actor", $"actor-{actorUserId:N}@example.test"));
        administrator.Add(new Group(groupId, $"Group {groupId:N}", actorUserId, requestId));
        administrator.Add(
            new Company(companyId, groupId, $"Company {companyId:N}", actorUserId, requestId)
        );
        administrator.Add(
            new Membership(membershipId, companyId, actorUserId, actorUserId, requestId)
        );
        administrator.Add(new Role(roleId, companyId, "owner", actorUserId, requestId));
        administrator.Add(
            new RoleAssignment(
                Guid.NewGuid(),
                companyId,
                roleId,
                membershipId,
                actorUserId,
                requestId
            )
        );
        if (grantEmployeeCreate)
        {
            var employeeCreate = await administrator.Capabilities.SingleAsync(item =>
                item.Name == "foundation.employees.create"
            );
            administrator.Add(
                new RoleCapability(
                    Guid.NewGuid(),
                    companyId,
                    roleId,
                    employeeCreate.Id,
                    CapabilityScope.Company,
                    actorUserId,
                    requestId
                )
            );
        }
        if (grantRolesAssign)
        {
            var rolesAssign = await administrator.Capabilities.SingleAsync(item =>
                item.Name == "foundation.roles.assign"
            );
            administrator.Add(
                new RoleCapability(
                    Guid.NewGuid(),
                    companyId,
                    roleId,
                    rolesAssign.Id,
                    CapabilityScope.Company,
                    actorUserId,
                    requestId
                )
            );
        }
        await administrator.SaveChangesAsync();
        return new Scenario(actorUserId, companyId, membershipId, roleId, groupId);
    }

    private static async Task<PlatformOperatorScenario> SeedPlatformOperatorAsync(
        FoundationPostgresFixture fixture
    )
    {
        var userId = Guid.NewGuid();
        var externalIdentityId = Guid.NewGuid();
        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            administrator.Add(
                new User(userId, "Platform operator", $"operator-{userId:N}@example.test")
            );
            administrator.Add(
                new ExternalIdentity(
                    externalIdentityId,
                    userId,
                    "https://project.supabase.co/auth/v1",
                    $"operator-{externalIdentityId:N}"
                )
            );
            await administrator.SaveChangesAsync();
        }

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO foundation.platform_operators (singleton, external_identity_id) VALUES (1, @external_identity_id)",
            connection
        );
        command.Parameters.AddWithValue("external_identity_id", externalIdentityId);
        await command.ExecuteNonQueryAsync();
        return new PlatformOperatorScenario(userId, externalIdentityId);
    }

    private static async Task SeedProvisioningReceiptWithDifferentRequestAsync(
        FoundationPostgresFixture fixture,
        Guid actorUserId,
        Scenario owner,
        Guid receiptRequestId
    )
    {
        var invitationId = Guid.NewGuid();
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.pending_owner_invitations (id, user_id, company_id, status)
            VALUES (@invitation_id, @owner_user_id, @company_id, 'pending_delivery');
            INSERT INTO foundation.organization_provisioning_receipts (
                id, actor_user_id, request_id, payload_hash, group_id, company_id,
                owner_user_id, owner_membership_id, owner_invitation_id)
            VALUES (
                @receipt_id, @actor_user_id, @request_id, @payload_hash, @group_id, @company_id,
                @owner_user_id, @owner_membership_id, @invitation_id);
            """,
            connection
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        command.Parameters.AddWithValue("owner_user_id", owner.ActorUserId);
        command.Parameters.AddWithValue("company_id", owner.CompanyId);
        command.Parameters.AddWithValue("receipt_id", Guid.NewGuid());
        command.Parameters.AddWithValue("actor_user_id", actorUserId);
        command.Parameters.AddWithValue("request_id", receiptRequestId);
        command.Parameters.AddWithValue("payload_hash", new string('a', 64));
        command.Parameters.AddWithValue("group_id", owner.GroupId);
        command.Parameters.AddWithValue("owner_membership_id", owner.MembershipId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Scenario(
        Guid ActorUserId,
        Guid CompanyId,
        Guid MembershipId,
        Guid RoleId,
        Guid GroupId
    );

    private sealed record PlatformOperatorScenario(Guid UserId, Guid ExternalIdentityId);
}
