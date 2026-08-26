using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class CompanyDefaultRolesMigrationTests
{
    private const string BeforeCompanyDefaultRoles = "20260824235249_AddAiReadCapability";
    private static readonly string[] OwnerGrantNames =
    [
        "foundation.employees.create",
        "foundation.files.confirm",
        "foundation.files.upload",
        "foundation.memberships.revoke",
        "foundation.roles.assign",
    ];
    private static readonly string[] AdminGrantNames =
    [
        "foundation.employees.create",
        "foundation.files.confirm",
        "foundation.files.upload",
        "foundation.roles.assign",
    ];
    private static readonly string[] MemberGrantNames = ["foundation.files.upload"];

    [Fact]
    public async Task Seeds_default_roles_idempotently_per_company()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(BeforeCompanyDefaultRoles);
        }
        var companyId = await SeedCompanyWithMembershipAsync(fixture);
        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync();
        }

        var seeded = await ReadLiveRoleGrantsAsync(fixture, companyId);
        Assert.Equal(AdminGrantNames, seeded["admin"]);
        Assert.Equal(MemberGrantNames, seeded["member"]);
        var adminRoleId = await ReadLiveRoleIdAsync(fixture, companyId, "admin");
        var memberRoleId = await ReadLiveRoleIdAsync(fixture, companyId, "member");

        await CallEnsureAsRuntimeAsync(fixture, companyId);
        await CallEnsureAsRuntimeAsync(fixture, companyId);

        var replayed = await ReadLiveRoleGrantsAsync(fixture, companyId);
        Assert.Equal(seeded.Keys.Order(), replayed.Keys.Order());
        Assert.Equal(AdminGrantNames, replayed["admin"]);
        Assert.Equal(MemberGrantNames, replayed["member"]);
        Assert.Equal(adminRoleId, await ReadLiveRoleIdAsync(fixture, companyId, "admin"));
        Assert.Equal(memberRoleId, await ReadLiveRoleIdAsync(fixture, companyId, "member"));
    }

    [Fact]
    public async Task Backfills_existing_companies_and_provisioning_seeds_new_company()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(BeforeCompanyDefaultRoles);
        }
        var legacyCompanyId = await SeedCompanyWithMembershipAsync(fixture);
        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync();
        }

        var backfilled = await ReadLiveRoleGrantsAsync(fixture, legacyCompanyId);
        Assert.Equal(AdminGrantNames, backfilled["admin"]);
        Assert.Equal(MemberGrantNames, backfilled["member"]);

        await SeedPlatformOperatorAsync(fixture);
        var provisioned = await ProvisionThroughServiceSequenceAsync(fixture, "Backfill Group", "Backfill HQ");
        var provisionedRoles = await ReadLiveRoleGrantsAsync(fixture, provisioned.CompanyId);
        Assert.Equal(OwnerGrantNames, provisionedRoles["owner"]);
        Assert.Equal(AdminGrantNames, provisionedRoles["admin"]);
        Assert.Equal(MemberGrantNames, provisionedRoles["member"]);
    }

    [Fact]
    public async Task Owner_role_grants_are_unchanged()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await SeedPlatformOperatorAsync(fixture);
        var provisioned = await ProvisionThroughServiceSequenceAsync(fixture, "Owner Group", "Owner HQ");

        var roles = await ReadLiveRoleGrantsAsync(fixture, provisioned.CompanyId);
        Assert.Equal(OwnerGrantNames, roles["owner"]);
        Assert.Contains("foundation.employees.create", roles["owner"]);

        await CallEnsureAsRuntimeAsync(fixture, provisioned.CompanyId);
        var replayed = await ReadLiveRoleGrantsAsync(fixture, provisioned.CompanyId);
        Assert.Equal(OwnerGrantNames, replayed["owner"]);
    }

    [Fact]
    public async Task Custom_role_with_reserved_name_is_left_untouched()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(BeforeCompanyDefaultRoles);
        }
        var actorUserId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var customRoleId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            administrator.Add(new User(actorUserId, "Actor", $"actor-{actorUserId:N}@example.test"));
            administrator.Add(new Group(groupId, $"Group {groupId:N}", actorUserId, requestId));
            administrator.Add(
                new Company(companyId, groupId, $"Company {companyId:N}", actorUserId, requestId)
            );
            administrator.Add(
                new Membership(Guid.NewGuid(), companyId, actorUserId, actorUserId, requestId)
            );
            administrator.Add(new Role(customRoleId, companyId, "admin", actorUserId, requestId));
            administrator.Add(
                new RoleCapability(
                    Guid.NewGuid(),
                    companyId,
                    customRoleId,
                    KnownCapabilities.FilesUploadId,
                    CapabilityScope.Company,
                    actorUserId,
                    requestId
                )
            );
            await administrator.SaveChangesAsync();
        }

        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync();
        }

        var roles = await ReadLiveRoleGrantsAsync(fixture, companyId);
        Assert.Equal(MemberGrantNames, roles["member"]);
        Assert.Equal(["foundation.files.upload"], roles["admin"]);
        Assert.Equal(customRoleId, await ReadLiveRoleIdAsync(fixture, companyId, "admin"));
    }

    [Fact]
    public async Task Function_security_boundary()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                function_row.prosecdef,
                pg_get_userbyid(function_row.proowner),
                function_row.proconfig,
                owner.rolcanlogin,
                owner.rolinherit,
                owner.rolbypassrls,
                has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege('vinx_migrator', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege(0::oid, function_row.oid, 'EXECUTE'),
                has_schema_privilege('vinx_permissions_owner', 'foundation', 'USAGE'),
                NOT has_schema_privilege('vinx_permissions_owner', 'foundation', 'CREATE'),
                NOT has_table_privilege('vinx_runtime', 'foundation.roles', 'INSERT'),
                NOT has_table_privilege('vinx_runtime', 'foundation.role_capabilities', 'INSERT')
            FROM pg_proc AS function_row
            JOIN pg_roles AS owner ON owner.oid = function_row.proowner
            WHERE function_row.oid =
                'foundation.ensure_company_default_roles(uuid)'::regprocedure;
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("vinx_permissions_owner", reader.GetString(1));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
        Assert.True(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
        Assert.True(reader.GetBoolean(12));
    }

    private static async Task<Guid> SeedCompanyWithMembershipAsync(FoundationPostgresFixture fixture)
    {
        var actorUserId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Add(new User(actorUserId, "Actor", $"actor-{actorUserId:N}@example.test"));
        administrator.Add(new Group(groupId, $"Group {groupId:N}", actorUserId, requestId));
        administrator.Add(
            new Company(companyId, groupId, $"Company {companyId:N}", actorUserId, requestId)
        );
        administrator.Add(
            new Membership(Guid.NewGuid(), companyId, actorUserId, actorUserId, requestId)
        );
        await administrator.SaveChangesAsync();
        return companyId;
    }

    /// <summary>
    /// Reproduz, na mesma transação, a sequência exata que
    /// <c>PlatformOrganizationProvisioningService.ExecuteAsync</c> executa no caminho
    /// de provisionamento (provisionamento, reserva do owner e papéis padrão).
    /// </summary>
    private static async Task<ProvisionedCompany> ProvisionThroughServiceSequenceAsync(
        FoundationPostgresFixture fixture,
        string groupName,
        string companyName
    )
    {
        var requestId = Guid.NewGuid();
        var ownerDisplayName = "Owner";
        var ownerEmail = "owner@example.test";
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.user_id', '00000000-0000-0000-0000-000000000001', true), set_config('app.external_identity_id', '00000000-0000-0000-0000-000000000002', true), set_config('app.mfa_assurance', 'aal2', true)",
            runtime,
            transaction))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            SELECT status, company_id, owner_membership_id
            FROM foundation.provision_platform_organization(
                @request_id, @group_name, @company_name, @owner_display_name, @owner_email, @payload_hash)
            """,
            runtime,
            transaction))
        {
            command.Parameters.AddWithValue("request_id", requestId);
            command.Parameters.AddWithValue("group_name", groupName);
            command.Parameters.AddWithValue("company_name", companyName);
            command.Parameters.AddWithValue("owner_display_name", ownerDisplayName);
            command.Parameters.AddWithValue("owner_email", ownerEmail);
            command.Parameters.AddWithValue(
                "payload_hash",
                PayloadHash(requestId, groupName, companyName, ownerDisplayName, ownerEmail));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("created", reader.GetString(0));
            var provisioned = new ProvisionedCompany(reader.GetGuid(1), reader.GetGuid(2));
            await reader.CloseAsync();

            await using var ownerEmployee = new NpgsqlCommand(
                "SELECT foundation.ensure_platform_owner_employee_create(@request_id, @company_id, @owner_membership_id)",
                runtime,
                transaction);
            ownerEmployee.Parameters.AddWithValue("request_id", requestId);
            ownerEmployee.Parameters.AddWithValue("company_id", provisioned.CompanyId);
            ownerEmployee.Parameters.AddWithValue("owner_membership_id", provisioned.OwnerMembershipId);
            await ownerEmployee.ExecuteNonQueryAsync();

            await using var defaultRoles = new NpgsqlCommand(
                "SELECT foundation.ensure_company_default_roles(@company_id)",
                runtime,
                transaction);
            defaultRoles.Parameters.AddWithValue("company_id", provisioned.CompanyId);
            await defaultRoles.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
            return provisioned;
        }
    }

    private static async Task CallEnsureAsRuntimeAsync(
        FoundationPostgresFixture fixture,
        Guid companyId
    )
    {
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT foundation.ensure_company_default_roles(@company_id)",
            runtime,
            transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<Dictionary<string, string[]>> ReadLiveRoleGrantsAsync(
        FoundationPostgresFixture fixture,
        Guid companyId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT role_record.name, capability.name
            FROM foundation.roles AS role_record
            LEFT JOIN foundation.role_capabilities AS grant_record
                ON grant_record.role_id = role_record.id
               AND grant_record.deleted_at IS NULL
            LEFT JOIN foundation.capabilities AS capability
                ON capability.id = grant_record.capability_id
            WHERE role_record.company_id = @company_id
              AND role_record.deleted_at IS NULL
            ORDER BY role_record.name, capability.name
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        await using var reader = await command.ExecuteReaderAsync();
        var grants = new Dictionary<string, List<string>>();
        while (await reader.ReadAsync())
        {
            var roleName = reader.GetString(0);
            var capabilityName = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!grants.TryGetValue(roleName, out var capabilities))
            {
                grants[roleName] = capabilities = [];
            }
            if (capabilityName is not null)
            {
                capabilities.Add(capabilityName);
            }
        }
        return grants.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Order().ToArray());
    }

    private static async Task<Guid> ReadLiveRoleIdAsync(
        FoundationPostgresFixture fixture,
        Guid companyId,
        string roleName
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id
            FROM foundation.roles
            WHERE company_id = @company_id
              AND name = @role_name
              AND deleted_at IS NULL
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("role_name", roleName);
        return (Guid)(await command.ExecuteScalarAsync())!;
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

    private static string PayloadHash(
        Guid requestId,
        string groupName,
        string companyName,
        string ownerDisplayName,
        string ownerEmail)
    {
        var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(
            new[]
            {
                requestId.ToString("D"),
                groupName,
                companyName,
                ownerDisplayName,
                ownerEmail,
            });
        return Convert.ToHexString(SHA256.HashData(canonicalPayload))
            .ToLowerInvariant();
    }

    private sealed record ProvisionedCompany(Guid CompanyId, Guid OwnerMembershipId);
}
