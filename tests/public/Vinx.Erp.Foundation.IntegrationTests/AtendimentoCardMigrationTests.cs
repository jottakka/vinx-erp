using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AtendimentoCardMigrationTests
{
    private const string BeforeAtendimentoMvp =
        "20260816012000_AddPlatformOrganizationProvisioning";

    private static readonly (Guid Id, string Name)[] ExpectedCapabilities =
    [
        (
            KnownCapabilities.ServiceDeskTicketReadQueueId,
            KnownCapabilities.ServiceDeskTicketReadQueue
        ),
        (KnownCapabilities.ServiceDeskTicketCreateId, KnownCapabilities.ServiceDeskTicketCreate),
        (KnownCapabilities.ServiceDeskTicketAssignId, KnownCapabilities.ServiceDeskTicketAssign),
        (KnownCapabilities.ServiceDeskTicketMoveId, KnownCapabilities.ServiceDeskTicketMove),
    ];

    [Fact]
    public async Task Migration_preserves_legacy_id_for_a_known_name_and_downgrade_leaves_it_intact()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeAtendimentoMvp);
        var legacyId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var insert = new NpgsqlCommand(
                "INSERT INTO foundation.capabilities (id, name, created_at) VALUES (@id, @name, clock_timestamp())",
                administrator
            )
        )
        {
            insert.Parameters.AddWithValue("id", legacyId);
            insert.Parameters.AddWithValue("name", KnownCapabilities.ServiceDeskTicketReadQueue);
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.Database.MigrateAsync();

        await using (var verify = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var select = new NpgsqlCommand(
                "SELECT id FROM foundation.capabilities WHERE name = @name",
                verify
            )
        )
        {
            select.Parameters.AddWithValue("name", KnownCapabilities.ServiceDeskTicketReadQueue);
            Assert.Equal(legacyId, await select.ExecuteScalarAsync());
        }

        await migrator.Database.MigrateAsync(BeforeAtendimentoMvp);

        await using var afterDowngrade = await fixture.OpenAdministratorConnectionAsync();
        await using var preserved = new NpgsqlCommand(
            """
            SELECT
                (SELECT id FROM foundation.capabilities WHERE name = @name),
                (SELECT count(*) FROM foundation.capabilities WHERE name = ANY (@other_names))
            """,
            afterDowngrade
        );
        preserved.Parameters.AddWithValue("name", KnownCapabilities.ServiceDeskTicketReadQueue);
        preserved.Parameters.AddWithValue(
            "other_names",
            ExpectedCapabilities.Skip(1).Select(capability => capability.Name).ToArray()
        );
        await using var reader = await preserved.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(legacyId, reader.GetGuid(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Migration_fails_closed_when_a_canonical_id_is_owned_by_another_name()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeAtendimentoMvp);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var insert = new NpgsqlCommand(
                "INSERT INTO foundation.capabilities (id, name, created_at) VALUES (@id, 'legacy.other', clock_timestamp())",
                administrator
            )
        )
        {
            insert.Parameters.AddWithValue("id", KnownCapabilities.ServiceDeskTicketReadQueueId);
            await insert.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync()
        );

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Downgrade_fails_closed_when_a_seeded_service_desk_capability_is_referenced()
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
        await using (
            var insert = new NpgsqlCommand(
                """
                INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (@group_id, 'Grupo', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (@company_id, @group_id, 'Empresa', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (@role_id, @company_id, 'Operador', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (gen_random_uuid(), @company_id, @role_id, @capability_id, 'company', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
                """,
                administrator
            )
        )
        {
            insert.Parameters.AddWithValue("group_id", groupId);
            insert.Parameters.AddWithValue("company_id", companyId);
            insert.Parameters.AddWithValue("role_id", roleId);
            insert.Parameters.AddWithValue("actor_id", actorId);
            insert.Parameters.AddWithValue("request_id", requestId);
            insert.Parameters.AddWithValue(
                "capability_id",
                KnownCapabilities.ServiceDeskTicketMoveId
            );
            await insert.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeAtendimentoMvp)
        );

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }
}
