using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Revisão item 1: <c>paguai.list_draft_approver_user_ids</c> roda SECURITY
/// DEFINER como vinx_permissions_owner sob FORCE RLS — a função PRECISA fixar
/// internamente app.company_id. Prova estrutural (dono + set_config) e
/// comportamental com contexto NÃO-admin de outra empresa (que, sem o fix,
/// devolveria zero destinatários).
/// </summary>
public sealed class PaguaiApproverAccessTests
{
    [Fact]
    public async Task Approver_directory_function_pins_owner_company_context_and_resolves_under_foreign_session()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        var creatorUserId = Guid.NewGuid();
        var approverUserId = Guid.NewGuid();
        var approverRoleId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var foreignCompanyId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            const string usersAndCompanies = """
                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (@creator, 'Criador', 'creator@example.test', clock_timestamp()),
                       (@approver, 'Aprovadora', 'approver@example.test', clock_timestamp());
                INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (@group_id, 'Grupo', @creator, @creator, @request_id, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (@company_id, @group_id, 'Vinx Obras', @creator, @creator, @request_id, clock_timestamp(), clock_timestamp()),
                       (@foreign_id, @group_id, 'Outra', @creator, @creator, @request_id, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (gen_random_uuid(), @company_id, @creator, @creator, @creator, @request_id, clock_timestamp(), clock_timestamp()),
                       (gen_random_uuid(), @company_id, @approver, @creator, @creator, @request_id, clock_timestamp(), clock_timestamp()),
                       (gen_random_uuid(), @foreign_id, @creator, @creator, @creator, @request_id, clock_timestamp(), clock_timestamp());
                """;
            await using var command = new NpgsqlCommand(usersAndCompanies, administrator);
            command.Parameters.AddWithValue("creator", creatorUserId);
            command.Parameters.AddWithValue("approver", approverUserId);
            command.Parameters.AddWithValue("group_id", groupId);
            command.Parameters.AddWithValue("request_id", requestId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("foreign_id", foreignCompanyId);
            await command.ExecuteNonQueryAsync();

            const string roleGrantAssignment = """
                INSERT INTO foundation.roles (
                    id, company_id, name, created_by_user_id, updated_by_user_id,
                    request_id, created_at, updated_at)
                VALUES (@role_id, @company_id, 'Paguai approver', @creator, @creator,
                        @request_id, clock_timestamp(), clock_timestamp());

                INSERT INTO foundation.role_capabilities (
                    id, company_id, role_id, capability_id, scope,
                    created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                SELECT gen_random_uuid(), @company_id, @role_id, capability.id, 'company',
                       @creator, @creator, @request_id, clock_timestamp(), clock_timestamp()
                FROM foundation.capabilities AS capability
                WHERE capability.name = 'paguai.draft.approve';

                INSERT INTO foundation.role_assignments (
                    id, company_id, role_id, membership_id, created_by_user_id,
                    updated_by_user_id, request_id, created_at, updated_at)
                SELECT gen_random_uuid(), @company_id, @role_id, membership.id, @creator,
                       @creator, @request_id, clock_timestamp(), clock_timestamp()
                FROM foundation.memberships AS membership
                WHERE membership.company_id = @company_id
                  AND membership.user_id = @approver;
                """;
            await using var grantCommand = new NpgsqlCommand(roleGrantAssignment, administrator);
            grantCommand.Parameters.AddWithValue("creator", creatorUserId);
            grantCommand.Parameters.AddWithValue("approver", approverUserId);
            grantCommand.Parameters.AddWithValue("company_id", companyId);
            grantCommand.Parameters.AddWithValue("request_id", requestId);
            grantCommand.Parameters.AddWithValue("role_id", approverRoleId);
            await grantCommand.ExecuteNonQueryAsync();
        }

        // Estrutura: dono correto + contexto de empresa fixado no corpo.
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var structure = new NpgsqlCommand(
            """
            SELECT pg_get_userbyid(function_row.proowner),
                   function_row.prosrc LIKE '%set_config(''app.company_id'', p_company_id::text, true)%'
            FROM pg_proc AS function_row
            WHERE function_row.oid =
                'paguai.list_draft_approver_user_ids(uuid, uuid)'::regprocedure
            """,
            administrator))
        {
            await using var reader = await structure.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("vinx_permissions_owner", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
        }

        // Comportamento sob sessão runtime com CONTEXTO DE OUTRA empresa: sem o
        // set_config interno o FORCE RLS filtraria tudo e retornaria vazio.
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @user_id, true), "
                + "set_config('app.company_id', @foreign_id, true)",
            runtime,
            transaction))
        {
            context.Parameters.AddWithValue("user_id", creatorUserId.ToString("D"));
            context.Parameters.AddWithValue("foreign_id", foreignCompanyId.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }

        await using var call = new NpgsqlCommand(
            "SELECT user_id FROM paguai.list_draft_approver_user_ids(@company_id, @exclude)",
            runtime,
            transaction);
        call.Parameters.AddWithValue("company_id", companyId);
        call.Parameters.AddWithValue("exclude", creatorUserId);
        await using var recipientReader = await call.ExecuteReaderAsync();
        var recipients = new List<Guid>();
        while (await recipientReader.ReadAsync())
        {
            recipients.Add(recipientReader.GetGuid(0));
        }

        Assert.Equal((IEnumerable<Guid>)[approverUserId], recipients);
    }
}
