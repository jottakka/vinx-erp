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
        "20260826150000_AddPaguaiRequestInvoiceFile";
    private const string AtendimentoCancellationMigration =
        "20260826151000_AddAtendimentoCancellationParticipantsAndAttachment";
    private const string BeforeServiceDeskTicketCancel =
        "20260826151000_AddAtendimentoCancellationParticipantsAndAttachment";

    private static readonly (Guid Id, string Name)[] ExpectedCapabilities =
    [
        (
            KnownCapabilities.ServiceDeskTicketReadQueueId,
            KnownCapabilities.ServiceDeskTicketReadQueue
        ),
        (KnownCapabilities.ServiceDeskTicketCreateId, KnownCapabilities.ServiceDeskTicketCreate),
        (KnownCapabilities.ServiceDeskTicketAssignId, KnownCapabilities.ServiceDeskTicketAssign),
        (KnownCapabilities.ServiceDeskTicketMoveId, KnownCapabilities.ServiceDeskTicketMove),
        (KnownCapabilities.ServiceDeskTicketCancelId, KnownCapabilities.ServiceDeskTicketCancel),
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
    public async Task Cancel_capability_seed_preserves_a_legacy_id_and_downgrade_removes_only_the_canonical_row()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeServiceDeskTicketCancel);
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
            insert.Parameters.AddWithValue("name", KnownCapabilities.ServiceDeskTicketCancel);
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
            select.Parameters.AddWithValue("name", KnownCapabilities.ServiceDeskTicketCancel);
            Assert.Equal(legacyId, await select.ExecuteScalarAsync());
        }

        await migrator.Database.MigrateAsync(BeforeServiceDeskTicketCancel);

        await using var afterDowngrade = await fixture.OpenAdministratorConnectionAsync();
        await using var preserved = new NpgsqlCommand(
            "SELECT id FROM foundation.capabilities WHERE name = @name",
            afterDowngrade
        );
        preserved.Parameters.AddWithValue("name", KnownCapabilities.ServiceDeskTicketCancel);
        Assert.Equal(legacyId, await preserved.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Cancel_capability_seed_fails_closed_when_the_canonical_id_is_owned_by_another_name()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeServiceDeskTicketCancel);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var insert = new NpgsqlCommand(
                "INSERT INTO foundation.capabilities (id, name, created_at) VALUES (@id, 'legacy.cancel', clock_timestamp())",
                administrator
            )
        )
        {
            insert.Parameters.AddWithValue("id", KnownCapabilities.ServiceDeskTicketCancelId);
            await insert.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync()
        );

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
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

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await AssertTablesRemainForcedAsync(
            verify,
            "atendimento_cards",
            "atendimento_card_history"
        );
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("attachment")]
    [InlineData("cancelled_card")]
    [InlineData("cancel_history")]
    public async Task Downgrade_fails_closed_with_new_atendimento_references_without_losing_data(
        string retainedReference
    )
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCancellationDowngradeDataAsync(fixture);

        switch (retainedReference)
        {
            case "participant":
                await InsertParticipantAsync(fixture, seeded);
                break;
            case "attachment":
                await AttachFileAsync(fixture, seeded);
                break;
            case "cancelled_card":
                await CancelCardAsync(fixture, seeded);
                break;
            case "cancel_history":
                await InsertCancellationHistoryAsync(fixture, seeded);
                break;
            default:
                throw new InvalidOperationException($"Unexpected retained reference: {retainedReference}.");
        }

        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await migrator.Database.MigrateAsync(BeforeAtendimentoCancellation)
            );
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var preserved = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM "__EFMigrationsHistory"
                    WHERE migration_id = @migration_id),
                EXISTS (
                    SELECT 1
                    FROM service_desk.atendimento_cards
                    WHERE id = @card_id),
                EXISTS (
                    SELECT 1
                    FROM service_desk.atendimento_card_participants
                    WHERE company_id = @company_id
                      AND card_id = @card_id
                      AND membership_id = @membership_id),
                EXISTS (
                    SELECT 1
                    FROM service_desk.atendimento_cards
                    WHERE id = @card_id
                      AND attachment_file_id = @file_id),
                EXISTS (
                    SELECT 1
                    FROM service_desk.atendimento_cards
                    WHERE id = @card_id
                      AND column_name = 'cancelado'
                      AND cancelled_at IS NOT NULL
                      AND cancelled_by_user_id = @actor_id
                      AND cancellation_reason = 'Motivo de downgrade.'),
                EXISTS (
                    SELECT 1
                    FROM service_desk.atendimento_card_history
                    WHERE id = @history_id
                      AND from_column = 'novo'
                      AND to_column = 'cancelado')
            """,
            verify
        );
        preserved.Parameters.AddWithValue("migration_id", AtendimentoCancellationMigration);
        preserved.Parameters.AddWithValue("card_id", seeded.CardId);
        preserved.Parameters.AddWithValue("company_id", seeded.CompanyId);
        preserved.Parameters.AddWithValue("membership_id", seeded.MembershipId);
        preserved.Parameters.AddWithValue("file_id", seeded.FileId);
        preserved.Parameters.AddWithValue("actor_id", seeded.ActorId);
        preserved.Parameters.AddWithValue("history_id", seeded.HistoryId);
        await using var reader = await preserved.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(retainedReference == "participant", reader.GetBoolean(2));
        Assert.Equal(retainedReference == "attachment", reader.GetBoolean(3));
        Assert.Equal(retainedReference == "cancelled_card", reader.GetBoolean(4));
        Assert.Equal(retainedReference == "cancel_history", reader.GetBoolean(5));
        await reader.CloseAsync();
        await AssertTablesRemainForcedAsync(
            verify,
            "atendimento_cards",
            "atendimento_card_history",
            "atendimento_card_participants"
        );
    }

    private static async Task<CancellationDowngradeSeed> SeedCancellationDowngradeDataAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo de downgrade", actorId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Empresa de downgrade",
            actorId,
            requestId
        );
        var actor = new User(actorId, "Ator de downgrade", "downgrade-actor@example.test");
        var membership = new Membership(
            Guid.NewGuid(),
            company.Id,
            actor.Id,
            actor.Id,
            Guid.NewGuid()
        );
        administrator.AddRange(actor, group, company, membership);
        await administrator.SaveChangesAsync();

        var cardId = Guid.NewGuid();
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO service_desk.atendimento_cards
                (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
            VALUES
                ({cardId}, {company.Id}, 'Cartão de downgrade', 'Preserva estado antes da reversão.', 'novo', {actor.Id}, {actor.Id}, {Guid.NewGuid()})
            """
        );

        return new CancellationDowngradeSeed(
            company.Id,
            actor.Id,
            membership.Id,
            cardId,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
    }

    private static async Task InsertParticipantAsync(
        FoundationPostgresFixture fixture,
        CancellationDowngradeSeed seeded
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO service_desk.atendimento_card_participants (company_id, card_id, membership_id)
            VALUES (@company_id, @card_id, @membership_id)
            """,
            administrator
        );
        insert.Parameters.AddWithValue("company_id", seeded.CompanyId);
        insert.Parameters.AddWithValue("card_id", seeded.CardId);
        insert.Parameters.AddWithValue("membership_id", seeded.MembershipId);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task AttachFileAsync(
        FoundationPostgresFixture fixture,
        CancellationDowngradeSeed seeded
    )
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var file = new FileRecord(
            seeded.FileId,
            seeded.CompanyId,
            seeded.MembershipId,
            "atendimento.pdf",
            ".pdf",
            42,
            "application/pdf",
            Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadLeft(64, 'a'),
            "service_desk_attachment",
            "web",
            DateTimeOffset.UtcNow.AddDays(1),
            seeded.ActorId,
            Guid.NewGuid()
        );
        file.MarkVerified(seeded.ActorId, Guid.NewGuid());
        file.MarkConfirmed(seeded.ActorId, Guid.NewGuid());
        administrator.Files.Add(file);
        await administrator.SaveChangesAsync();
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_desk.atendimento_cards SET attachment_file_id = {seeded.FileId} WHERE id = {seeded.CardId}"
        );
    }

    private static async Task CancelCardAsync(
        FoundationPostgresFixture fixture,
        CancellationDowngradeSeed seeded
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var update = new NpgsqlCommand(
            """
            UPDATE service_desk.atendimento_cards
            SET column_name = 'cancelado',
                cancelled_at = clock_timestamp(),
                cancelled_by_user_id = @actor_id,
                cancellation_reason = 'Motivo de downgrade.'
            WHERE id = @card_id
            """,
            administrator
        );
        update.Parameters.AddWithValue("actor_id", seeded.ActorId);
        update.Parameters.AddWithValue("card_id", seeded.CardId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    private static async Task InsertCancellationHistoryAsync(
        FoundationPostgresFixture fixture,
        CancellationDowngradeSeed seeded
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO service_desk.atendimento_card_history
                (id, company_id, card_id, from_column, to_column, actor_user_id, request_id)
            VALUES
                (@history_id, @company_id, @card_id, 'novo', 'cancelado', @actor_id, @request_id)
            """,
            administrator
        );
        insert.Parameters.AddWithValue("history_id", seeded.HistoryId);
        insert.Parameters.AddWithValue("company_id", seeded.CompanyId);
        insert.Parameters.AddWithValue("card_id", seeded.CardId);
        insert.Parameters.AddWithValue("actor_id", seeded.ActorId);
        insert.Parameters.AddWithValue("request_id", Guid.NewGuid());
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task AssertTablesRemainForcedAsync(
        NpgsqlConnection administrator,
        params string[] tableNames
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT relation.relname, relation.relforcerowsecurity
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'service_desk'
              AND relation.relname = ANY (@table_names)
            ORDER BY relation.relname
            """,
            administrator
        );
        command.Parameters.AddWithValue("table_names", tableNames);
        await using var reader = await command.ExecuteReaderAsync();
        var forced = new List<(string Table, bool IsForced)>();
        while (await reader.ReadAsync())
        {
            forced.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        Assert.Equal(tableNames.OrderBy(table => table, StringComparer.Ordinal), forced.Select(row => row.Table));
        Assert.All(forced, row => Assert.True(row.IsForced, $"{row.Table} must keep FORCE RLS."));
    }

    private sealed record ActorProfile(Guid UserId, string DisplayName);

    private sealed record CancellationDowngradeSeed(
        Guid CompanyId,
        Guid ActorId,
        Guid MembershipId,
        Guid CardId,
        Guid FileId,
        Guid HistoryId
    );
}
