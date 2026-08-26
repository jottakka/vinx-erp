using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AtendimentoCardSchemaTests
{
    [Fact]
    public async Task Runtime_rls_reads_only_cards_for_the_transaction_company_and_never_without_context()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
            );
            var visible = await runtime
                .Database.SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM service_desk.atendimento_cards ORDER BY id"
                )
                .ToListAsync();

            Assert.Equal([seeded.CardAId], visible);
            Assert.DoesNotContain(seeded.CardBId, visible);
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var withoutContext = await runtime
                .Database.SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM service_desk.atendimento_cards ORDER BY id"
                )
                .ToListAsync();

            Assert.Empty(withoutContext);
        }
    }

    [Fact]
    public async Task Runtime_rls_reads_participants_only_for_the_transaction_company_and_never_without_context()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
            );
            var visible = await runtime.Database.SqlQuery<Guid>(
                $"SELECT membership_id AS \"Value\" FROM service_desk.atendimento_card_participants ORDER BY membership_id"
            ).ToListAsync();

            Assert.Equal([seeded.MembershipAId], visible);
            Assert.DoesNotContain(seeded.MembershipBId, visible);
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var withoutContext = await runtime.Database.SqlQuery<Guid>(
                $"SELECT membership_id AS \"Value\" FROM service_desk.atendimento_card_participants ORDER BY membership_id"
            ).ToListAsync();

            Assert.Empty(withoutContext);
        }
    }

    [Fact]
    public async Task Runtime_has_no_delete_grant_for_atendimento_tables()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
        );

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM service_desk.atendimento_cards WHERE id = {seeded.CardAId}"
            )
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Runtime_has_no_update_or_delete_grant_for_atendimento_participants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
        );

        var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM service_desk.atendimento_card_participants WHERE card_id = {seeded.CardAId}"
            )
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Assignment_options_function_is_security_definer_scoped_and_does_not_grant_runtime_user_reads()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true); SELECT set_config('app.user_id', {seeded.ActorId.ToString()}, true);"
            );
            var options = await runtime.Database.SqlQuery<AssignmentOption>(
                $"SELECT membership_id, display_name FROM foundation.current_company_membership_options()"
            ).ToListAsync();

            Assert.Equal([new AssignmentOption(seeded.MembershipAId, "Atendimento actor")], options);
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var directRead = await Assert.ThrowsAsync<PostgresException>(async () =>
                await runtime.Database.SqlQuery<string>(
                    $"SELECT display_name AS \"Value\" FROM foundation.users"
                ).ToListAsync()
            );
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, directRead.SqlState);
        }

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT p.prosecdef, p.proconfig::text, r.rolname, has_function_privilege('vinx_runtime', 'foundation.current_company_membership_options()', 'EXECUTE'), NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) AS acl WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE') FROM pg_proc p INNER JOIN pg_namespace n ON n.oid = p.pronamespace INNER JOIN pg_roles r ON r.oid = p.proowner WHERE n.nspname = 'foundation' AND p.proname = 'current_company_membership_options';",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Contains("search_path=foundation, pg_catalog", reader.GetString(1), StringComparison.Ordinal);
        Assert.Equal("vinx_authorization_owner", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    private static async Task<SeededCards> SeedCardsAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo Atendimento", actorId, requestId);
        var companyA = new Company(Guid.NewGuid(), group.Id, "Empresa A", actorId, requestId);
        var companyB = new Company(Guid.NewGuid(), group.Id, "Empresa B", actorId, requestId);
        var actor = new User(actorId, "Atendimento actor", "atendimento-actor@example.test");
        var membershipA = new Membership(
            Guid.NewGuid(),
            companyA.Id,
            actorId,
            actorId,
            Guid.NewGuid()
        );
        var membershipB = new Membership(
            Guid.NewGuid(),
            companyB.Id,
            actorId,
            actorId,
            Guid.NewGuid()
        );
        administrator.AddRange(actor, group, companyA, companyB, membershipA, membershipB);
        await administrator.SaveChangesAsync();

        var cardAId = Guid.NewGuid();
        var cardBId = Guid.NewGuid();
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO service_desk.atendimento_cards
                (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
            VALUES
                ({cardAId}, {companyA.Id}, 'Cartão A', 'Empresa A.', 'novo', {actorId}, {actorId}, {Guid.NewGuid()}),
                ({cardBId}, {companyB.Id}, 'Cartão B', 'Empresa B.', 'novo', {actorId}, {actorId}, {Guid.NewGuid()});
            INSERT INTO service_desk.atendimento_card_participants (company_id, card_id, membership_id)
            VALUES
                ({companyA.Id}, {cardAId}, {membershipA.Id}),
                ({companyB.Id}, {cardBId}, {membershipB.Id});
            """
        );
        return new SeededCards(
            companyA.Id,
            companyB.Id,
            cardAId,
            cardBId,
            actorId,
            membershipA.Id,
            membershipB.Id
        );
    }

    private sealed record AssignmentOption(Guid MembershipId, string DisplayName);

    private sealed record SeededCards(
        Guid CompanyAId,
        Guid CompanyBId,
        Guid CardAId,
        Guid CardBId,
        Guid ActorId,
        Guid MembershipAId,
        Guid MembershipBId
    );
}
