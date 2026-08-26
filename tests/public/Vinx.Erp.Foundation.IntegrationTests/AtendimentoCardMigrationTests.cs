using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AtendimentoCardMigrationTests
{
    private const string BeforeAtendimentoMvp =
        "20260816012000_AddPlatformOrganizationProvisioning";
    private const string BeforeAtendimentoCancellation =
        "20260826112330_AddPaguaiRequestInvoiceFile";

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

    [Fact]
    public async Task Migration_creates_cancellation_attachment_and_participant_schema_without_broad_runtime_grants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('service_desk.atendimento_card_participants') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'service_desk'
                      AND table_name = 'atendimento_cards'
                      AND column_name = 'attachment_file_id'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'service_desk'
                      AND table_name = 'atendimento_cards'
                      AND column_name = 'cancelled_at'),
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_participants', 'SELECT,INSERT'),
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_participants', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_participants', 'DELETE');
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
    }

    [Fact]
    public async Task Empty_downgrade_preserves_the_preexisting_runtime_actor_profile_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actor = new User(Guid.NewGuid(), "Perfil preservado", "profile@example.test");

        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            administrator.Users.Add(actor);
            await administrator.SaveChangesAsync();
        }

        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(BeforeAtendimentoCancellation);
        }

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.Id.ToString()}, true)"
        );
        var profile = await runtime.Database.SqlQuery<ActorProfile>(
            $"SELECT user_id, display_name FROM foundation.current_actor_profile()"
        ).SingleAsync();

        Assert.Equal(actor.Id, profile.UserId);
        Assert.Equal("Perfil preservado", profile.DisplayName);
    }

    private sealed record ActorProfile(Guid UserId, string DisplayName);
}
