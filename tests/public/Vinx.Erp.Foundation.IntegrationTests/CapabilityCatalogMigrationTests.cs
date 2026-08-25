using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class CapabilityCatalogMigrationTests
{
    private const string BeforeCapabilityCatalog = "20260810231256_AddSessionGateAndSelfRevocation";
    private const string BeforeFilesReadOcr = "20260823150831_AddExternalIdentityReprovisioning";
    private const string BeforeAiRead = "20260824164540_AddFilesReadOcrCapability";

    private static readonly (Guid Id, string Name)[] ExpectedCapabilities =
    [
        (KnownCapabilities.RolesAssignId, KnownCapabilities.RolesAssign),
        (KnownCapabilities.FilesUploadId, KnownCapabilities.FilesUpload),
        (KnownCapabilities.FilesConfirmId, KnownCapabilities.FilesConfirm),
        (KnownCapabilities.MembershipsRevokeId, KnownCapabilities.MembershipsRevoke),
        (KnownCapabilities.FilesReadOcrId, KnownCapabilities.FilesReadOcr),
        (KnownCapabilities.AiReadId, KnownCapabilities.AiRead)
    ];

    [Fact]
    public async Task Migration_seeds_each_known_capability_once_without_runtime_mutation_privileges()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await AssertCatalogMigrationAppliedAsync(fixture);
        await AssertCatalogAsync(fixture);
        await AssertRuntimePrivilegesAsync(fixture);

        await fixture.MigrateAsync();
        await AssertCatalogAsync(fixture);
    }

    [Fact]
    public async Task Migration_preserves_the_existing_id_for_a_known_name_when_downgraded()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeCapabilityCatalog);
        var existingId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO foundation.capabilities (id, name, created_at) VALUES (@id, @name, clock_timestamp())",
            administrator))
        {
            insert.Parameters.AddWithValue("id", existingId);
            insert.Parameters.AddWithValue("name", KnownCapabilities.RolesAssign);
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.Database.MigrateAsync();

        await AssertCatalogMigrationAppliedAsync(fixture);

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var select = new NpgsqlCommand(
            "SELECT id FROM foundation.capabilities WHERE name = @name", verify);
        select.Parameters.AddWithValue("name", KnownCapabilities.RolesAssign);
        Assert.Equal(existingId, await select.ExecuteScalarAsync());

        await migrator.Database.MigrateAsync(BeforeCapabilityCatalog);

        await using var afterDowngrade = await fixture.OpenAdministratorConnectionAsync();
        await using var preserved = new NpgsqlCommand(
            """
            SELECT
                (SELECT id FROM foundation.capabilities WHERE name = @name),
                (SELECT count(*) FROM foundation.capabilities WHERE name = ANY (@other_names))
            """, afterDowngrade);
        preserved.Parameters.AddWithValue("name", KnownCapabilities.RolesAssign);
        preserved.Parameters.AddWithValue("other_names", ExpectedCapabilities.Skip(1).Select(capability => capability.Name).ToArray());
        await using var reader = await preserved.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(existingId, reader.GetGuid(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Migration_fails_explicitly_when_a_known_id_belongs_to_a_different_name()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeCapabilityCatalog);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO foundation.capabilities (id, name, created_at) VALUES (@id, 'legacy.other', clock_timestamp())",
            administrator))
        {
            insert.Parameters.AddWithValue("id", ExpectedCapabilities[0].Id);
            await insert.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Downgrade_fails_closed_when_a_seeded_capability_is_referenced()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var companyId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, 'Grupo', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company_id, @group_id, 'Empresa', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@role_id, @company_id, 'Operador', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @role_id, @capability_id, 'company', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            """, administrator))
        {
            insert.Parameters.AddWithValue("group_id", groupId);
            insert.Parameters.AddWithValue("company_id", companyId);
            insert.Parameters.AddWithValue("role_id", roleId);
            insert.Parameters.AddWithValue("actor_id", actorId);
            insert.Parameters.AddWithValue("request_id", requestId);
            insert.Parameters.AddWithValue("capability_id", ExpectedCapabilities[0].Id);
            await insert.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeCapabilityCatalog));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Files_read_ocr_downgrade_fails_closed_when_the_capability_is_referenced()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var companyId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, 'Grupo OCR', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company_id, @group_id, 'Empresa OCR', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@role_id, @company_id, 'Leitor OCR', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @role_id, @capability_id, 'company', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            """, administrator))
        {
            insert.Parameters.AddWithValue("group_id", groupId);
            insert.Parameters.AddWithValue("company_id", companyId);
            insert.Parameters.AddWithValue("role_id", roleId);
            insert.Parameters.AddWithValue("actor_id", actorId);
            insert.Parameters.AddWithValue("request_id", requestId);
            insert.Parameters.AddWithValue("capability_id", KnownCapabilities.FilesReadOcrId);
            await insert.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeFilesReadOcr));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Ai_read_downgrade_fails_closed_when_the_capability_is_referenced()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var companyId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, 'Grupo AI', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company_id, @group_id, 'Empresa AI', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@role_id, @company_id, 'Leitor AI', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @role_id, @capability_id, 'company', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            """, administrator))
        {
            insert.Parameters.AddWithValue("group_id", groupId);
            insert.Parameters.AddWithValue("company_id", companyId);
            insert.Parameters.AddWithValue("role_id", roleId);
            insert.Parameters.AddWithValue("actor_id", actorId);
            insert.Parameters.AddWithValue("request_id", requestId);
            insert.Parameters.AddWithValue("capability_id", KnownCapabilities.AiReadId);
            await insert.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeAiRead));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    private static async Task AssertCatalogAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        foreach (var (id, name) in ExpectedCapabilities)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM foundation.capabilities WHERE name = @name", administrator);
            command.Parameters.AddWithValue("name", name);
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            await using var idCommand = new NpgsqlCommand(
                "SELECT id FROM foundation.capabilities WHERE name = @name", administrator);
            idCommand.Parameters.AddWithValue("name", name);
            Assert.Equal(id, await idCommand.ExecuteScalarAsync());
        }
    }

    private static async Task AssertCatalogMigrationAppliedAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '%_AddFoundationCapabilityCatalog')",
            administrator);
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    private static async Task AssertRuntimePrivilegesAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_runtime', 'foundation.capabilities', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.capabilities', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.capabilities', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.capabilities', 'DELETE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }
}
