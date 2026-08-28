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

    private static async Task<SeededCards> SeedCardsAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo Atendimento", actorId, requestId);
        var companyA = new Company(Guid.NewGuid(), group.Id, "Empresa A", actorId, requestId);
        var companyB = new Company(Guid.NewGuid(), group.Id, "Empresa B", actorId, requestId);
        var actor = new User(actorId, "Atendimento actor", "atendimento-actor@example.test");
        administrator.AddRange(actor, group, companyA, companyB);
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
            """
        );
        return new SeededCards(companyA.Id, companyB.Id, cardAId, cardBId);
    }

    private sealed record SeededCards(Guid CompanyAId, Guid CompanyBId, Guid CardAId, Guid CardBId);
}
