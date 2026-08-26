using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftMigrationTests
{
    private const string BeforePaguaiInvoiceFile = "20260824235249_AddAiReadCapability";
    private const string PaguaiInvoiceFileMigration = "20260826112330_AddPaguaiRequestInvoiceFile";

    [Fact]
    public async Task Empty_downgrade_removes_the_invoice_file_column_and_preserves_forced_rls()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(PaguaiInvoiceFileMigration);

        await migrator.Database.MigrateAsync(BeforePaguaiInvoiceFile);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'paguai'
                      AND table_name = 'requests'
                      AND column_name = 'invoice_file_id'),
                relation.relforcerowsecurity
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'paguai'
              AND relation.relname = 'requests'
            """,
            administrator
        );
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Downgrade_refuses_a_referenced_invoice_without_losing_the_migration_row_or_forced_rls()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(PaguaiInvoiceFileMigration);
        var draft = await SeedReferencedInvoiceDraftAsync(fixture);

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforePaguaiInvoiceFile)
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM "__EFMigrationsHistory"
                    WHERE migration_id = @migration_id),
                EXISTS (
                    SELECT 1
                    FROM paguai.requests
                    WHERE id = @draft_id
                      AND invoice_file_id = @invoice_file_id),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'paguai'
                      AND table_name = 'requests'
                      AND column_name = 'invoice_file_id'),
                relation.relforcerowsecurity
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'paguai'
              AND relation.relname = 'requests'
            """,
            administrator
        );
        verify.Parameters.AddWithValue("migration_id", PaguaiInvoiceFileMigration);
        verify.Parameters.AddWithValue("draft_id", draft.Id);
        verify.Parameters.AddWithValue("invoice_file_id", draft.InvoiceFileId!.Value);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    private static async Task<PaguaiRequest> SeedReferencedInvoiceDraftAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actor = new User(Guid.NewGuid(), "Ator de downgrade", "paguai-downgrade@example.test");
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo de downgrade", actor.Id, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Empresa de downgrade",
            actor.Id,
            requestId
        );
        var membership = new Membership(
            Guid.NewGuid(),
            company.Id,
            actor.Id,
            actor.Id,
            requestId
        );
        administrator.AddRange(actor, group, company, membership);
        await administrator.SaveChangesAsync();

        var invoice = new FileRecord(
            Guid.NewGuid(),
            company.Id,
            membership.Id,
            "invoice.pdf",
            ".pdf",
            42,
            "application/pdf",
            new string('a', 64),
            "paguai_request_invoice",
            "integration-test",
            DateTimeOffset.UtcNow.AddDays(1),
            actor.Id,
            Guid.NewGuid()
        );
        invoice.MarkVerified(actor.Id, Guid.NewGuid());
        invoice.MarkConfirmed(actor.Id, Guid.NewGuid());
        administrator.Files.Add(invoice);
        await administrator.SaveChangesAsync();

        var draft = new PaguaiRequest
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            RequesterUserId = actor.Id,
            RequestId = Guid.NewGuid(),
            Title = "Rascunho de downgrade",
            Description = "Preserva a referência da fatura durante a reversão.",
            Amount = 42.00m,
            Currency = "BRL",
            DueDate = new DateOnly(2026, 9, 1),
            BeneficiaryDisplayName = "Fornecedor de teste",
            ProofRequirement = "required",
            InvoiceFileId = invoice.Id,
        };
        administrator.PaguaiRequests.Add(draft);
        await administrator.SaveChangesAsync();
        return draft;
    }
}
