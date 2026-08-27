using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftApproverDirectoryTests
{
    private const string DraftApproveCapability = "paguai.draft.approve";
    private const string BeforeDraftApproverDirectory =
        "20260826225942_AddAtendimentoOperationalMetadata";
    private static readonly Guid DraftApproveCapabilityId =
        Guid.Parse("e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a");

    [Fact]
    public async Task Worker_lists_only_explicit_non_requester_approvers_from_the_requested_company()
    {
        await using var fixture = await StartFixtureAsync();
        var actorId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var otherCompanyApproverId = Guid.NewGuid();
        var companyId = await SeedCompanyAsync(fixture, actorId, "Alfa");
        var otherCompanyId = await SeedCompanyAsync(fixture, actorId, "Beta");

        await SeedApproverAsync(fixture, companyId, requesterId, actorId);
        await SeedApproverAsync(fixture, companyId, approverId, actorId);
        await SeedApproverAsync(fixture, otherCompanyId, otherCompanyApproverId, actorId);

        var result = await ListApproversAsync(fixture, companyId, requesterId);

        Assert.Equal([approverId], result);
    }

    [Fact]
    public async Task Capability_and_directory_have_the_canonical_least_privilege_contract()
    {
        await using var fixture = await StartFixtureAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                capability.id,
                function_row.prosecdef,
                pg_get_userbyid(function_row.proowner),
                function_row.proconfig,
                owner.rolcanlogin,
                owner.rolinherit,
                owner.rolbypassrls,
                has_function_privilege('vinx_worker', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege('vinx_migrator', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege(0::oid, function_row.oid, 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        function_row.proacl,
                        acldefault('f', function_row.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee NOT IN (
                          function_row.proowner,
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_worker'))),
                has_schema_privilege('vinx_worker', 'paguai', 'USAGE'),
                NOT has_schema_privilege('vinx_worker', 'paguai', 'CREATE'),
                NOT has_schema_privilege('vinx_permissions_owner', 'paguai', 'CREATE'),
                NOT has_table_privilege('vinx_worker', 'foundation.memberships', 'SELECT'),
                NOT has_table_privilege('vinx_worker', 'foundation.role_assignments', 'SELECT'),
                NOT has_table_privilege('vinx_worker', 'foundation.roles', 'SELECT'),
                NOT has_table_privilege('vinx_worker', 'foundation.role_capabilities', 'SELECT'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_class AS table_row
                    INNER JOIN pg_namespace AS table_schema
                        ON table_schema.oid = table_row.relnamespace
                    WHERE table_schema.nspname = 'foundation'
                      AND table_row.relname IN (
                          'memberships', 'role_assignments', 'roles', 'role_capabilities')
                      AND NOT table_row.relforcerowsecurity)
            FROM pg_proc AS function_row
            INNER JOIN pg_roles AS owner ON owner.oid = function_row.proowner
            INNER JOIN foundation.capabilities AS capability
                ON capability.name = 'paguai.draft.approve'
            WHERE function_row.oid =
                'paguai.list_draft_approver_user_ids(uuid,uuid)'::regprocedure;
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(DraftApproveCapabilityId, reader.GetGuid(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("vinx_permissions_owner", reader.GetString(2));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(3)
        );
        for (var ordinal = 4; ordinal <= 6; ordinal++)
        {
            Assert.False(reader.GetBoolean(ordinal));
        }
        for (var ordinal = 7; ordinal <= 19; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Directory_rejects_null_or_empty_identifiers(
        bool nullCompany,
        bool emptyExcludedUser
    )
    {
        await using var fixture = await StartFixtureAsync();
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT * FROM paguai.list_draft_approver_user_ids(@company_id, @exclude_user_id)",
            worker
        );
        command.Parameters.Add(
            new NpgsqlParameter("company_id", NpgsqlDbType.Uuid)
            {
                Value = nullCompany ? DBNull.Value : Guid.NewGuid(),
            }
        );
        command.Parameters.AddWithValue(
            "exclude_user_id",
            emptyExcludedUser ? Guid.Empty : Guid.NewGuid()
        );

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, exception.SqlState);
    }

    [Fact]
    public async Task Default_admin_is_not_an_approver_without_an_explicit_financial_grant()
    {
        await using var fixture = await StartFixtureAsync();
        var actorId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var explicitApproverId = Guid.NewGuid();
        var companyId = await SeedCompanyAsync(fixture, actorId, "Defaults");
        await SeedMembershipAsync(fixture, companyId, requesterId, actorId);
        var adminMembershipId = await SeedMembershipAsync(fixture, companyId, adminId, actorId);
        await EnsureDefaultRolesAsync(fixture, companyId);

        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            var adminRole = await administrator.Roles.SingleAsync(role =>
                role.CompanyId == companyId && role.Name == "admin"
            );
            administrator.Add(
                new RoleAssignment(
                    Guid.NewGuid(),
                    companyId,
                    adminRole.Id,
                    adminMembershipId,
                    actorId,
                    Guid.NewGuid()
                )
            );
            await administrator.SaveChangesAsync();
            Assert.DoesNotContain(
                await administrator
                    .RoleCapabilities.Where(grant =>
                        grant.CompanyId == companyId
                        && grant.RoleId == adminRole.Id
                        && grant.DeletedAt == null
                    )
                    .Select(grant => grant.Capability.Name)
                    .ToListAsync(),
                name => name == DraftApproveCapability
            );
        }

        await SeedApproverAsync(fixture, companyId, explicitApproverId, actorId);
        Assert.Equal(
            [explicitApproverId],
            await ListApproversAsync(fixture, companyId, requesterId)
        );
    }

    [Fact]
    public async Task Directory_excludes_revoked_memberships_assignments_roles_and_grants()
    {
        await using var fixture = await StartFixtureAsync();
        var actorId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var companyId = await SeedCompanyAsync(fixture, actorId, "State");
        var membershipRevoked = await SeedApproverAsync(
            fixture,
            companyId,
            Guid.NewGuid(),
            actorId
        );
        var assignmentRevoked = await SeedApproverAsync(
            fixture,
            companyId,
            Guid.NewGuid(),
            actorId
        );
        var roleRevoked = await SeedApproverAsync(
            fixture,
            companyId,
            Guid.NewGuid(),
            actorId
        );
        var grantRevoked = await SeedApproverAsync(
            fixture,
            companyId,
            Guid.NewGuid(),
            actorId
        );
        var active = await SeedApproverAsync(fixture, companyId, Guid.NewGuid(), actorId);

        await SoftDeleteAsync(fixture, "memberships", membershipRevoked.MembershipId, actorId);
        await SoftDeleteAsync(
            fixture,
            "role_assignments",
            assignmentRevoked.AssignmentId,
            actorId
        );
        await SoftDeleteAsync(fixture, "roles", roleRevoked.RoleId, actorId);
        await SoftDeleteAsync(
            fixture,
            "role_capabilities",
            grantRevoked.GrantId,
            actorId
        );

        Assert.Equal(
            [active.UserId],
            await ListApproversAsync(fixture, companyId, requesterId)
        );
    }

    [Fact]
    public async Task Duplicate_grants_are_deduplicated_across_concurrent_directory_reads()
    {
        await using var fixture = await StartFixtureAsync();
        var actorId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var companyId = await SeedCompanyAsync(fixture, actorId, "Concurrency");
        var approver = await SeedApproverAsync(fixture, companyId, approverId, actorId);
        var duplicate = await AddEligibleRoleAsync(
            fixture,
            companyId,
            approver.MembershipId,
            actorId
        );

        var reads = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                ListApproversAsync(fixture, companyId, requesterId)
            )
        );
        Assert.All(reads, result => Assert.Equal([approverId], result));

        await SoftDeleteAsync(fixture, "role_capabilities", approver.GrantId, actorId);
        Assert.Equal([approverId], await ListApproversAsync(fixture, companyId, requesterId));
        await SoftDeleteAsync(fixture, "role_capabilities", duplicate.GrantId, actorId);
        Assert.Empty(await ListApproversAsync(fixture, companyId, requesterId));
    }

    [Fact]
    public async Task Downgrade_refuses_references_then_removes_only_the_new_contracts()
    {
        await using var fixture = await StartFixtureAsync();
        var actorId = Guid.NewGuid();
        var companyId = await SeedCompanyAsync(fixture, actorId, "Downgrade");
        var approver = await SeedApproverAsync(fixture, companyId, Guid.NewGuid(), actorId);

        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.Database.MigrateAsync(BeforeDraftApproverDirectory)
            );
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }
        await AssertDirectoryExistsAsync(fixture, expected: true);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var deleteGrant = new NpgsqlCommand(
                "DELETE FROM foundation.role_capabilities WHERE id = @grant_id",
                administrator
            )
        )
        {
            deleteGrant.Parameters.AddWithValue("grant_id", approver.GrantId);
            await deleteGrant.ExecuteNonQueryAsync();
        }
        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(BeforeDraftApproverDirectory);
        }

        await AssertDirectoryExistsAsync(fixture, expected: false);
        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                NOT EXISTS (
                    SELECT 1 FROM foundation.capabilities
                    WHERE name = 'paguai.draft.approve'),
                to_regprocedure('foundation.ensure_company_default_roles(uuid)') IS NOT NULL,
                to_regprocedure('foundation.list_company_members_with_roles(uuid)') IS NOT NULL,
                EXISTS (
                    SELECT 1 FROM foundation.capabilities
                    WHERE name = 'service_desk.ticket.cancel')
            """,
            verify
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Guid> SeedCompanyAsync(
        FoundationPostgresFixture fixture,
        Guid actorId,
        string name
    )
    {
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        if (!await administrator.Users.AnyAsync(user => user.Id == actorId))
        {
            administrator.Add(
                new User(actorId, "Seed actor", $"actor-{actorId:N}@example.test")
            );
        }
        administrator.Add(new Group(groupId, $"Group {name}", actorId, requestId));
        administrator.Add(new Company(companyId, groupId, name, actorId, requestId));
        await administrator.SaveChangesAsync();
        return companyId;
    }

    private static async Task<ApproverState> SeedApproverAsync(
        FoundationPostgresFixture fixture,
        Guid companyId,
        Guid userId,
        Guid actorId
    )
    {
        var requestId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Add(
            new User(userId, $"User {userId:N}", $"user-{userId:N}@example.test")
        );
        administrator.Add(new Membership(membershipId, companyId, userId, actorId, requestId));
        administrator.Add(
            new Role(roleId, companyId, $"explicit approver {userId:N}", actorId, requestId)
        );
        var assignmentId = Guid.NewGuid();
        administrator.Add(
            new RoleAssignment(
                assignmentId,
                companyId,
                roleId,
                membershipId,
                actorId,
                requestId
            )
        );
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == DraftApproveCapability
        );
        var grantId = Guid.NewGuid();
        administrator.Add(
            new RoleCapability(
                grantId,
                companyId,
                roleId,
                capability.Id,
                CapabilityScope.Company,
                actorId,
                requestId
            )
        );
        await administrator.SaveChangesAsync();
        return new ApproverState(userId, membershipId, roleId, assignmentId, grantId);
    }

    private static async Task<Guid> SeedMembershipAsync(
        FoundationPostgresFixture fixture,
        Guid companyId,
        Guid userId,
        Guid actorId
    )
    {
        var membershipId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Add(
            new User(userId, $"User {userId:N}", $"user-{userId:N}@example.test")
        );
        administrator.Add(
            new Membership(membershipId, companyId, userId, actorId, Guid.NewGuid())
        );
        await administrator.SaveChangesAsync();
        return membershipId;
    }

    private static async Task<ApproverState> AddEligibleRoleAsync(
        FoundationPostgresFixture fixture,
        Guid companyId,
        Guid membershipId,
        Guid actorId
    )
    {
        var roleId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        var membership = await administrator.Memberships.SingleAsync(item =>
            item.Id == membershipId
        );
        var capability = await administrator.Capabilities.SingleAsync(item =>
            item.Name == DraftApproveCapability
        );
        administrator.Add(
            new Role(roleId, companyId, $"approver {roleId:N}", actorId, requestId)
        );
        administrator.Add(
            new RoleAssignment(
                assignmentId,
                companyId,
                roleId,
                membershipId,
                actorId,
                requestId
            )
        );
        administrator.Add(
            new RoleCapability(
                grantId,
                companyId,
                roleId,
                capability.Id,
                CapabilityScope.Company,
                actorId,
                requestId
            )
        );
        await administrator.SaveChangesAsync();
        return new ApproverState(
            membership.UserId,
            membershipId,
            roleId,
            assignmentId,
            grantId
        );
    }

    private static async Task EnsureDefaultRolesAsync(
        FoundationPostgresFixture fixture,
        Guid companyId
    )
    {
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT foundation.ensure_company_default_roles(@company_id)",
            runtime
        );
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SoftDeleteAsync(
        FoundationPostgresFixture fixture,
        string table,
        Guid id,
        Guid actorId
    )
    {
        Assert.Contains(
            table,
            new[] { "memberships", "role_assignments", "roles", "role_capabilities" }
        );
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE foundation.{table}
            SET deleted_at = clock_timestamp(),
                deleted_by_user_id = @actor_id,
                deleted_reason = 'test revocation',
                updated_at = clock_timestamp()
            WHERE id = @id
            """,
            administrator
        );
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertDirectoryExistsAsync(
        FoundationPostgresFixture fixture,
        bool expected
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regprocedure('paguai.list_draft_approver_user_ids(uuid,uuid)') IS NOT NULL",
            administrator
        );
        Assert.Equal(expected, (bool)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<IReadOnlyList<Guid>> ListApproversAsync(
        FoundationPostgresFixture fixture,
        Guid companyId,
        Guid requesterId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT user_id FROM paguai.list_draft_approver_user_ids(@company_id, @exclude_user_id)",
            worker
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("exclude_user_id", requesterId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<Guid>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetGuid(0));
        }
        return result;
    }

    private sealed record ApproverState(
        Guid UserId,
        Guid MembershipId,
        Guid RoleId,
        Guid AssignmentId,
        Guid GrantId
    );
}
