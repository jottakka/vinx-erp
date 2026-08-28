using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Composição da migração de ticketing do Atendimento (fatia P6): prova o
/// contrato Up/Down escrito à mão — policies e grants simétricos das quatro
/// tabelas, o ciclo de vida da função SECURITY DEFINER de resolução de nomes
/// (sem grant de coluna de users ao runtime), o trigger de canais e o
/// downgrade de priority de volta ao shape do main.
/// </summary>
public sealed class AtendimentoTicketingMigrationCompositionTests
{
    private const string MainOperationalMetadata =
        "20260826225942_AddAtendimentoOperationalMetadata";
    private const string BranchMigration = "20260827000000_AddAtendimentoTicketing";

    [Fact]
    public async Task Ticketing_applies_after_main_and_downgrades_without_losing_main_contracts()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();

        await migrator.Database.MigrateAsync(MainOperationalMetadata);
        Assert.Equal(0, await CountBranchHistoryAsync(fixture));
        await AssertDowngradedShapeAsync(fixture);

        await migrator.Database.MigrateAsync(BranchMigration);
        Assert.Equal(1, await CountBranchHistoryAsync(fixture));
        await AssertTicketingShapeAsync(fixture);

        await migrator.Database.MigrateAsync(MainOperationalMetadata);
        Assert.Equal(0, await CountBranchHistoryAsync(fixture));
        await AssertDowngradedShapeAsync(fixture);
    }

    [Fact]
    public async Task Resolve_card_actor_names_serves_runtime_without_direct_users_access()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        var actorId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (@actor, 'Bea Timeline', 'bea-timeline@example.test', clock_timestamp());
                INSERT INTO foundation.groups (
                    id, name, created_by_user_id, updated_by_user_id, request_id,
                    created_at, updated_at)
                VALUES (@group, 'Grupo Timeline', @actor, @actor, @request,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.companies (
                    id, group_id, name, created_by_user_id, updated_by_user_id, request_id,
                    created_at, updated_at)
                VALUES (@company, @group, 'Empresa Timeline', @actor, @actor, @request,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.memberships (
                    id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id,
                    created_at, updated_at)
                VALUES (@membership, @company, @actor, @actor, @actor, @request,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO service_desk.atendimento_cards (
                    id, company_id, title, description, column_name,
                    created_by_user_id, updated_by_user_id, request_id)
                VALUES (@card, @company, 'Cartão timeline', 'Composição', 'novo', @actor, @actor, @request);
                INSERT INTO service_desk.atendimento_card_events (
                    id, company_id, card_id, action_kind, actor_membership_id, payload_digest)
                VALUES (gen_random_uuid(), @company, @card, 'created', @membership, @digest);
                """,
                administrator
            );
            seed.Parameters.AddWithValue("actor", actorId);
            seed.Parameters.AddWithValue("group", Guid.NewGuid());
            seed.Parameters.AddWithValue("company", companyId);
            seed.Parameters.AddWithValue("membership", membershipId);
            seed.Parameters.AddWithValue("card", cardId);
            seed.Parameters.AddWithValue("request", Guid.NewGuid());
            seed.Parameters.AddWithValue("digest", new string('a', 64));
            await seed.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        // Postura do main preservada: o runtime não lê users diretamente.
        await using (var directRead = new NpgsqlCommand(
            "SELECT display_name FROM foundation.users",
            runtime))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(
                () => directRead.ExecuteReaderAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using (var resolved = new NpgsqlCommand(
            """
            SELECT actor_membership_id, display_name
            FROM service_desk.resolve_card_actor_names(@company, @card)
            """,
            runtime))
        {
            resolved.Parameters.AddWithValue("company", companyId);
            resolved.Parameters.AddWithValue("card", cardId);
            await using var reader = await resolved.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(membershipId, reader.GetGuid(0));
            Assert.Equal("Bea Timeline", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }

        await using (var foreignCompany = new NpgsqlCommand(
            """
            SELECT count(*) FROM service_desk.resolve_card_actor_names(@company, @card)
            """,
            runtime))
        {
            foreignCompany.Parameters.AddWithValue("company", Guid.NewGuid());
            foreignCompany.Parameters.AddWithValue("card", cardId);
            Assert.Equal(0L, Convert.ToInt64(await foreignCompany.ExecuteScalarAsync()));
        }

        await using (var invalidParameters = new NpgsqlCommand(
            "SELECT count(*) FROM service_desk.resolve_card_actor_names(NULL, NULL)",
            runtime))
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(
                () => invalidParameters.ExecuteScalarAsync());
            Assert.Equal("22023", invalid.SqlState);
        }
    }

    private static async Task<int> CountBranchHistoryAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = @id",
            administrator
        );
        command.Parameters.AddWithValue("id", BranchMigration);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>Contrato completo da migração aplicada: tabelas, RLS, grants,
    /// função de resolução, trigger e shape de priority.</summary>
    private static async Task AssertTicketingShapeAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('service_desk.atendimento_channels') IS NOT NULL,
                to_regclass('service_desk.atendimento_categories') IS NOT NULL,
                to_regclass('service_desk.atendimento_card_watchers') IS NOT NULL,
                to_regclass('service_desk.atendimento_card_events') IS NOT NULL,
                (SELECT count(*) FROM pg_class AS relation
                    WHERE relation.relname IN (
                        'atendimento_channels', 'atendimento_categories',
                        'atendimento_card_watchers', 'atendimento_card_events')
                        AND relation.relrowsecurity AND relation.relforcerowsecurity) = 4,
                (SELECT count(*) FROM pg_policies
                    WHERE schemaname = 'service_desk'
                        AND policyname IN (
                            'atendimento_channels_company_isolation',
                            'atendimento_categories_company_isolation',
                            'atendimento_card_watchers_company_isolation',
                            'atendimento_card_events_company_isolation')) = 4,
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_channels', 'SELECT'),
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_categories', 'SELECT,INSERT,UPDATE'),
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_watchers', 'SELECT,INSERT,DELETE'),
                has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_events', 'SELECT,INSERT'),
                NOT has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_events', 'UPDATE'),
                NOT has_table_privilege('vinx_runtime', 'service_desk.atendimento_card_events', 'DELETE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.users', 'id', 'SELECT'),
                NOT has_column_privilege('vinx_runtime', 'foundation.users', 'display_name', 'SELECT'),
                to_regprocedure('service_desk.resolve_card_actor_names(uuid, uuid)') IS NOT NULL,
                (SELECT function.prosecdef FROM pg_proc AS function
                    WHERE function.oid = 'service_desk.resolve_card_actor_names(uuid, uuid)'::regprocedure),
                (SELECT pg_get_userbyid(function.proowner) = 'vinx_permissions_owner'
                    FROM pg_proc AS function
                    WHERE function.oid = 'service_desk.resolve_card_actor_names(uuid, uuid)'::regprocedure),
                (SELECT function.proconfig = ARRAY['search_path=pg_catalog, service_desk, pg_temp']
                    FROM pg_proc AS function
                    WHERE function.oid = 'service_desk.resolve_card_actor_names(uuid, uuid)'::regprocedure),
                has_function_privilege(
                    'vinx_runtime', 'service_desk.resolve_card_actor_names(uuid, uuid)', 'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_worker', 'service_desk.resolve_card_actor_names(uuid, uuid)', 'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_migrator', 'service_desk.resolve_card_actor_names(uuid, uuid)', 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_proc AS function
                    CROSS JOIN LATERAL aclexplode(
                        COALESCE(function.proacl, acldefault('f', function.proowner))) AS privilege
                    WHERE function.oid = 'service_desk.resolve_card_actor_names(uuid, uuid)'::regprocedure
                        AND privilege.grantee = 0
                        AND privilege.privilege_type = 'EXECUTE'),
                has_table_privilege(
                    'vinx_permissions_owner', 'service_desk.atendimento_card_events', 'SELECT'),
                has_schema_privilege('vinx_permissions_owner', 'service_desk', 'USAGE'),
                EXISTS (
                    SELECT 1 FROM pg_trigger AS trigger
                    JOIN pg_class AS relation ON relation.oid = trigger.tgrelid
                    WHERE relation.relname = 'companies'
                        AND trigger.tgname = 'trg_seed_company_lookup_defaults'),
                to_regprocedure('service_desk.seed_company_lookup_defaults_trigger()') IS NOT NULL,
                (SELECT shape_column.character_maximum_length = 20 AND shape_column.is_nullable = 'YES'
                    FROM information_schema.columns AS shape_column
                    WHERE table_schema = 'service_desk'
                        AND table_name = 'atendimento_cards'
                        AND column_name = 'priority'),
                EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'service_desk'
                        AND table_name = 'atendimento_cards'
                        AND column_name = 'requester_person_ref')
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(reader.GetBoolean(index), $"ticketing shape column {index} expected true");
        }
    }

    /// <summary>Shape do main pré-ramo: sem tabelas novas, sem função, sem
    /// trigger, priority varchar(10) NOT NULL DEFAULT 'media', sem grant de
    /// users ao runtime e capabilities da fatia removidas.</summary>
    private static async Task AssertDowngradedShapeAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('service_desk.atendimento_channels') IS NULL,
                to_regclass('service_desk.atendimento_categories') IS NULL,
                to_regclass('service_desk.atendimento_card_watchers') IS NULL,
                to_regclass('service_desk.atendimento_card_events') IS NULL,
                to_regprocedure('service_desk.resolve_card_actor_names(uuid, uuid)') IS NULL,
                to_regprocedure('service_desk.seed_company_lookup_defaults_trigger()') IS NULL,
                NOT EXISTS (
                    SELECT 1 FROM pg_trigger AS trigger
                    JOIN pg_class AS relation ON relation.oid = trigger.tgrelid
                    WHERE relation.relname = 'companies'
                        AND trigger.tgname = 'trg_seed_company_lookup_defaults'),
                NOT has_schema_privilege('vinx_permissions_owner', 'service_desk', 'USAGE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.users', 'id', 'SELECT'),
                NOT has_column_privilege('vinx_runtime', 'foundation.users', 'display_name', 'SELECT'),
                (SELECT shape_column.character_maximum_length = 10
                        AND shape_column.is_nullable = 'NO'
                        AND shape_column.column_default = '''media''::character varying'
                    FROM information_schema.columns AS shape_column
                    WHERE table_schema = 'service_desk'
                        AND table_name = 'atendimento_cards'
                        AND column_name = 'priority'),
                NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'service_desk'
                        AND table_name = 'atendimento_cards'
                        AND column_name IN ('requester_person_ref', 'category_ref_id', 'channel_ref_id')),
                NOT EXISTS (
                    SELECT 1 FROM foundation.capabilities
                    WHERE name IN (
                        'service_desk.ticket.fields',
                        'service_desk.ticket.watchers',
                        'service_desk.lookup.admin',
                        'directory.read'))
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(reader.GetBoolean(index), $"downgraded shape column {index} expected true");
        }
    }
}
