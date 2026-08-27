using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class E2eFixtureSeederTests
{
    [Theory]
    [InlineData("maria", "b1000000-0000-4000-8000-000000000001", "b2000000-0000-4000-8000-000000000001", "e2e-maria", "Maria Silva", "maria.silva@example.test")]
    [InlineData("beatriz", "b1000000-0000-4000-8000-000000000002", "b2000000-0000-4000-8000-000000000002", "e2e-beatriz", "Beatriz Costa", "beatriz.costa@example.test")]
    [InlineData("joao", "b1000000-0000-4000-8000-000000000003", "b2000000-0000-4000-8000-000000000003", "e2e-joao", "João Santos", "joao.santos@example.test")]
    [InlineData("clara", "b1000000-0000-4000-8000-000000000004", "b2000000-0000-4000-8000-000000000004", "e2e-clara", "Clara Oliveira", "clara.oliveira@example.test")]
    public void Profile_catalog_resolves_the_four_allowlisted_profiles(
        string name,
        string userId,
        string externalIdentityId,
        string subject,
        string displayName,
        string email)
    {
        Assert.True(E2eFixtureProfileCatalog.TryGet(name, out var profile));
        Assert.NotNull(profile);
        Assert.Equal(
            new E2eFixtureProfile(
                name,
                Guid.Parse(userId),
                Guid.Parse(externalIdentityId),
                subject,
                displayName,
                email),
            profile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Maria")]
    [InlineData("maria ")]
    [InlineData("operator")]
    public void Profile_catalog_rejects_unknown_or_noncanonical_names(string name)
    {
        Assert.False(E2eFixtureProfileCatalog.TryGet(name, out var profile));
        Assert.Null(profile);
    }

    [Fact]
    public async Task SeedAsync_creates_the_rich_multi_company_dataset_atomically()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await InsertCanonicalGroupSentinelAsync(fixture);
        var partialFailure = await Assert.ThrowsAsync<PostgresException>(
            () => E2eFixtureSeeder.SeedAsync(fixture, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, partialFailure.SqlState);
        Assert.Equal(
            (Users: 0L, ExternalIdentities: 0L, Groups: 1L, GroupName: "preexisting sentinel"),
            await ReadRollbackStateAsync(fixture));

        await RemoveCanonicalGroupSentinelAsync(fixture);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => E2eFixtureSeeder.SeedAsync(fixture, new CancellationToken(canceled: true)));
        Assert.Equal(0, await CountSeedTargetRowsAsync(fixture));

        var receipt = await E2eFixtureSeeder.SeedAsync(fixture, CancellationToken.None);
        await E2eFixtureSeeder.VerifyAsync(fixture, receipt, CancellationToken.None);

        Assert.Equal(
            new E2eDatasetReceipt(
                Guid.Parse("a1000000-0000-4000-8000-000000000001"),
                Guid.Parse("a2000000-0000-4000-8000-000000000001"),
                Guid.Parse("a2000000-0000-4000-8000-000000000002"),
                Guid.Parse("a2000000-0000-4000-8000-000000000003"),
                Guid.Parse("f4000000-0000-4000-8000-000000000001")),
            receipt);

        using var beforeSecondSeed = await ReadDatasetSummaryAsync(fixture);
        var root = beforeSecondSeed.RootElement;
        Assert.Equal(
            [1L, 3L, 4L, 4L, 5L, 3L, 3L, 5L, 5L, 18L, 5L, 4L, 5L, 4L, 1L, 0L, 3L],
            root.GetProperty("counts").EnumerateArray().Select(value => value.GetInt64()));
        Assert.Equal("Alfa,Beta", root.GetProperty("maria_companies").GetString());
        Assert.Equal(
            "Alfa/Atendimento agent=foundation.employees.create|service_desk.ticket.assign|" +
            "service_desk.ticket.create|service_desk.ticket.move|service_desk.ticket.read_queue;" +
            "Alfa/Operations=ai.read|foundation.employees.create|paguai.draft.create|paguai.draft.read|" +
            "service_desk.ticket.assign|service_desk.ticket.create|service_desk.ticket.move|" +
            "service_desk.ticket.read_queue;Beta/Finance operator=paguai.draft.create|" +
            "paguai.draft.read;Beta/Reader=paguai.draft.read|service_desk.ticket.read_queue;" +
            "Gama/Restricted=service_desk.ticket.read_queue",
            root.GetProperty("role_capabilities").GetString());
        Assert.Equal(
            "groups=a1000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000001;" +
            "companies=a2000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000002," +
            "a2000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000003," +
            "a2000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000004;" +
            "memberships=b3000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000005," +
            "b3000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000006," +
            "b3000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000007," +
            "b3000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000008," +
            "b3000000-0000-4000-8000-000000000005/f4000000-0000-4000-8000-000000000009;" +
            "units=c1000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000010," +
            "c1000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000011," +
            "c1000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000012;" +
            "teams=c2000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000013," +
            "c2000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000014," +
            "c2000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000015;" +
            "roles=d1000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000016," +
            "d1000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000017," +
            "d1000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000018," +
            "d1000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000019," +
            "d1000000-0000-4000-8000-000000000005/f4000000-0000-4000-8000-000000000020;" +
            "assignments=d2000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000021," +
            "d2000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000022," +
            "d2000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000023," +
            "d2000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000024," +
            "d2000000-0000-4000-8000-000000000005/f4000000-0000-4000-8000-000000000025;" +
            "grants=d3000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000026," +
            "d3000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000027," +
            "d3000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000028," +
            "d3000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000029," +
            "d3000000-0000-4000-8000-000000000005/f4000000-0000-4000-8000-000000000030," +
            "d3000000-0000-4000-8000-000000000006/f4000000-0000-4000-8000-000000000031," +
            "d3000000-0000-4000-8000-000000000007/f4000000-0000-4000-8000-000000000032," +
            "d3000000-0000-4000-8000-000000000008/f4000000-0000-4000-8000-000000000033," +
            "d3000000-0000-4000-8000-000000000009/f4000000-0000-4000-8000-000000000034," +
            "d3000000-0000-4000-8000-000000000010/f4000000-0000-4000-8000-000000000035," +
            "d3000000-0000-4000-8000-000000000011/f4000000-0000-4000-8000-000000000036," +
            "d3000000-0000-4000-8000-000000000012/f4000000-0000-4000-8000-000000000037," +
            "d3000000-0000-4000-8000-000000000013/f4000000-0000-4000-8000-000000000038," +
            "d3000000-0000-4000-8000-000000000014/f4000000-0000-4000-8000-000000000039," +
            "d3000000-0000-4000-8000-000000000015/f4000000-0000-4000-8000-000000000040," +
            "d3000000-0000-4000-8000-000000000016/f4000000-0000-4000-8000-000000000041," +
            "d3000000-0000-4000-8000-000000000017/f4000000-0000-4000-8000-000000000042," +
            "d3000000-0000-4000-8000-000000000018/f4000000-0000-4000-8000-000000000065;" +
            "employees=e1000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000043," +
            "e1000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000044," +
            "e1000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000045," +
            "e1000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000046," +
            "e1000000-0000-4000-8000-000000000005/f4000000-0000-4000-8000-000000000047;" +
            "paguai=f1000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000048," +
            "f1000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000049," +
            "f1000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000050," +
            "f1000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000051;" +
            "cards=f2000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000052," +
            "f2000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000053," +
            "f2000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000054," +
            "f2000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000055," +
            "f2000000-0000-4000-8000-000000000005/f4000000-0000-4000-8000-000000000056;" +
            "notifications=f3000000-0000-4000-8000-000000000001/f4000000-0000-4000-8000-000000000057/f4000000-0000-4000-8000-000000000061," +
            "f3000000-0000-4000-8000-000000000002/f4000000-0000-4000-8000-000000000058/f4000000-0000-4000-8000-000000000062," +
            "f3000000-0000-4000-8000-000000000003/f4000000-0000-4000-8000-000000000059/f4000000-0000-4000-8000-000000000063," +
            "f3000000-0000-4000-8000-000000000004/f4000000-0000-4000-8000-000000000060/f4000000-0000-4000-8000-000000000064",
            root.GetProperty("canonical_ids").GetString());
        Assert.Equal("Alfa:2,Beta:2,Gama:1", root.GetProperty("employees").GetString());
        Assert.Equal("Alfa:2,Beta:2", root.GetProperty("paguai").GetString());
        Assert.Equal(
            "f1000000-0000-4000-8000-000000000001=Alfa|Maria Silva," +
            "f1000000-0000-4000-8000-000000000002=Alfa|Beatriz Costa," +
            "f1000000-0000-4000-8000-000000000003=Beta|Maria Silva," +
            "f1000000-0000-4000-8000-000000000004=Beta|João Santos",
            root.GetProperty("paguai_ownership").GetString());
        Assert.Equal("Alfa:3,Beta:1,Gama:1", root.GetProperty("cards").GetString());
        Assert.Equal(
            "concluido:1,em_andamento:2,novo:2",
            root.GetProperty("card_columns").GetString());
        Assert.Equal(
            "Clara Oliveira:1,Maria Silva:3",
            root.GetProperty("notifications").GetString());
        Assert.Equal(
            "f3000000-0000-4000-8000-000000000001=Alfa|Maria Silva|/paguai|Maria Silva," +
            "f3000000-0000-4000-8000-000000000002=Alfa|Maria Silva|/atendimento|unread," +
            "f3000000-0000-4000-8000-000000000003=Beta|Maria Silva|/paguai|unread," +
            "f3000000-0000-4000-8000-000000000004=Gama|Clara Oliveira|/atendimento|unread",
            root.GetProperty("notification_ownership").GetString());
        Assert.Equal(
            "Maria Silva|e2e-maria|maria.silva@example.test;" +
            "Beatriz Costa|e2e-beatriz|beatriz.costa@example.test;" +
            "João Santos|e2e-joao|joao.santos@example.test;" +
            "Clara Oliveira|e2e-clara|clara.oliveira@example.test",
            root.GetProperty("profiles").GetString());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => E2eFixtureSeeder.SeedAsync(fixture, CancellationToken.None));

        Assert.Equal("e2e fixture database is not empty", exception.Message);
        using var afterSecondSeed = await ReadDatasetSummaryAsync(fixture);
        Assert.True(JsonElement.DeepEquals(root, afterSecondSeed.RootElement));
    }

    private static async Task InsertCanonicalGroupSentinelAsync(FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups
                (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES
                ('a1000000-0000-4000-8000-000000000001', 'preexisting sentinel',
                 'b1000000-0000-4000-8000-000000000001',
                 'b1000000-0000-4000-8000-000000000001',
                 'f4000000-0000-4000-8000-000000000099',
                 '2026-08-01T12:00:00Z', '2026-08-01T12:00:00Z')
            """,
            connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(long Users, long ExternalIdentities, long Groups, string GroupName)>
        ReadRollbackStateAsync(FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.users),
                (SELECT count(*) FROM foundation.external_identities),
                (SELECT count(*) FROM foundation.groups),
                (SELECT name FROM foundation.groups
                    WHERE id = 'a1000000-0000-4000-8000-000000000001')
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static async Task RemoveCanonicalGroupSentinelAsync(FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM foundation.groups WHERE id = 'a1000000-0000-4000-8000-000000000001'",
            connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<long> CountSeedTargetRowsAsync(FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.users)
                + (SELECT count(*) FROM foundation.external_identities)
                + (SELECT count(*) FROM foundation.groups)
                + (SELECT count(*) FROM foundation.companies)
                + (SELECT count(*) FROM foundation.memberships)
                + (SELECT count(*) FROM foundation.units)
                + (SELECT count(*) FROM foundation.teams)
                + (SELECT count(*) FROM foundation.roles)
                + (SELECT count(*) FROM foundation.role_assignments)
                + (SELECT count(*) FROM foundation.role_capabilities)
                + (SELECT count(*) FROM foundation.employees)
                + (SELECT count(*) FROM paguai.requests)
                + (SELECT count(*) FROM service_desk.atendimento_cards)
                + (SELECT count(*) FROM notifications.notification_intents)
            """,
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<JsonDocument> ReadDatasetSummaryAsync(
        FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT jsonb_build_object(
                'counts', jsonb_build_array(
                    (SELECT count(*) FROM foundation.groups),
                    (SELECT count(*) FROM foundation.companies),
                    (SELECT count(*) FROM foundation.users),
                    (SELECT count(*) FROM foundation.external_identities),
                    (SELECT count(*) FROM foundation.memberships),
                    (SELECT count(*) FROM foundation.units),
                    (SELECT count(*) FROM foundation.teams),
                    (SELECT count(*) FROM foundation.roles),
                    (SELECT count(*) FROM foundation.role_assignments),
                    (SELECT count(*) FROM foundation.role_capabilities),
                    (SELECT count(*) FROM foundation.employees),
                    (SELECT count(*) FROM paguai.requests),
                    (SELECT count(*) FROM service_desk.atendimento_cards),
                    (SELECT count(*) FROM notifications.notification_intents),
                    (SELECT count(*) FROM notifications.notification_intents WHERE read_at IS NOT NULL),
                    (SELECT count(*) FROM foundation.memberships
                        WHERE user_id = @maria AND company_id = @gama),
                    (SELECT count(DISTINCT requester_user_id) FROM paguai.requests)),
                'maria_companies', (SELECT string_agg(company.name, ',' ORDER BY company.name)
                    FROM foundation.memberships AS membership
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE membership.user_id = @maria),
                'role_capabilities', (SELECT string_agg(
                        company_name || '/' || role_name || '=' || capabilities, ';'
                        ORDER BY company_name, role_name)
                    FROM (SELECT company.name AS company_name, role.name AS role_name,
                            string_agg(capability.name, '|' ORDER BY capability.name) AS capabilities
                        FROM foundation.roles AS role
                        JOIN foundation.companies AS company ON company.id = role.company_id
                        JOIN foundation.role_capabilities AS grant_row
                            ON (grant_row.company_id, grant_row.role_id) = (role.company_id, role.id)
                        JOIN foundation.capabilities AS capability
                            ON capability.id = grant_row.capability_id
                        GROUP BY company.name, role.name) AS matrix),
                'canonical_ids', (SELECT string_agg(kind || '=' || ids, ';' ORDER BY ordinal)
                    FROM (
                        SELECT 1 AS ordinal, 'groups' AS kind,
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id) AS ids
                            FROM foundation.groups
                        UNION ALL SELECT 2, 'companies',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.companies
                        UNION ALL SELECT 3, 'memberships',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.memberships
                        UNION ALL SELECT 4, 'units',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id) AS ids
                            FROM foundation.units
                        UNION ALL SELECT 5, 'teams',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.teams
                        UNION ALL SELECT 6, 'roles',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.roles
                        UNION ALL SELECT 7, 'assignments',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.role_assignments
                        UNION ALL SELECT 8, 'grants',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.role_capabilities
                        UNION ALL SELECT 9, 'employees',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM foundation.employees
                        UNION ALL SELECT 10, 'paguai',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM paguai.requests
                        UNION ALL SELECT 11, 'cards',
                            string_agg(id::text || '/' || request_id::text, ',' ORDER BY id)
                            FROM service_desk.atendimento_cards
                        UNION ALL SELECT 12, 'notifications',
                            string_agg(id::text || '/' || correlation_id::text || '/' ||
                                provider_attempt_id::text, ',' ORDER BY id)
                            FROM notifications.notification_intents) AS signatures),
                'employees', (SELECT string_agg(summary.name || ':' || summary.total, ',' ORDER BY summary.name)
                    FROM (SELECT company.name, count(*)::text AS total
                        FROM foundation.employees AS row
                        JOIN foundation.companies AS company ON company.id = row.company_id
                        GROUP BY company.name) AS summary),
                'paguai', (SELECT string_agg(summary.name || ':' || summary.total, ',' ORDER BY summary.name)
                    FROM (SELECT company.name, count(*)::text AS total
                        FROM paguai.requests AS row
                        JOIN foundation.companies AS company ON company.id = row.company_id
                        GROUP BY company.name) AS summary),
                'paguai_ownership', (SELECT string_agg(
                        request.id::text || '=' || company.name || '|' || requester.display_name,
                        ',' ORDER BY request.id)
                    FROM paguai.requests AS request
                    JOIN foundation.companies AS company ON company.id = request.company_id
                    JOIN foundation.users AS requester ON requester.id = request.requester_user_id),
                'cards', (SELECT string_agg(summary.name || ':' || summary.total, ',' ORDER BY summary.name)
                    FROM (SELECT company.name, count(*)::text AS total
                        FROM service_desk.atendimento_cards AS row
                        JOIN foundation.companies AS company ON company.id = row.company_id
                        GROUP BY company.name) AS summary),
                'card_columns', (SELECT string_agg(column_name || ':' || total, ',' ORDER BY column_name)
                    FROM (SELECT column_name, count(*)::text AS total
                        FROM service_desk.atendimento_cards GROUP BY column_name) AS summary),
                'notifications', (SELECT string_agg(display_name || ':' || total, ',' ORDER BY display_name)
                    FROM (SELECT recipient.display_name, count(*)::text AS total
                        FROM notifications.notification_intents AS intent
                        JOIN foundation.users AS recipient ON recipient.id = intent.recipient_user_id
                        GROUP BY recipient.display_name) AS summary),
                'notification_ownership', (SELECT string_agg(
                        intent.id::text || '=' || company.name || '|' || recipient.display_name ||
                            '|' || intent.deep_link_selector || '|' ||
                            COALESCE(reader.display_name, 'unread'),
                        ',' ORDER BY intent.id)
                    FROM notifications.notification_intents AS intent
                    JOIN foundation.companies AS company ON company.id = intent.company_id
                    JOIN foundation.users AS recipient ON recipient.id = intent.recipient_user_id
                    LEFT JOIN foundation.users AS reader ON reader.id = intent.read_by_user_id),
                'profiles', (SELECT string_agg(
                        user_row.display_name || '|' || identity_row.subject || '|' || user_row.email,
                        ';' ORDER BY user_row.id)
                    FROM foundation.users AS user_row
                    JOIN foundation.external_identities AS identity_row
                        ON identity_row.user_id = user_row.id))::text
            """,
            connection);
        command.Parameters.AddWithValue(
            "maria",
            Guid.Parse("b1000000-0000-4000-8000-000000000001"));
        command.Parameters.AddWithValue(
            "gama",
            Guid.Parse("a2000000-0000-4000-8000-000000000003"));
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
    }
}
