using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftMigrationTests
{
    private const string BeforePaguaiInvoiceFile = "20260824235249_AddAiReadCapability";
    private const string PaguaiInvoiceFileMigration = "20260826150000_AddPaguaiRequestInvoiceFile";
    private const string BeforeCandidateCompanyContext =
        "20260826151000_AddAtendimentoCancellationParticipantsAndAttachment";
    private const string CandidateCompanyContextMigration =
        "20260826153000_BindPaguaiInboundCandidateToCompanyContext";

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

    [Fact]
    public async Task Candidate_read_requires_company_context_and_downgrade_fails_closed()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(CandidateCompanyContextMigration);
        var seed = await SeedInvoiceCandidateAsync(fixture);

        await using var gated = await fixture.OpenAdministratorConnectionAsync();
        await using var gatedTransaction = await gated.BeginTransactionAsync();
        try
        {
            await SetLocalContextAsync(gated, "app.user_id", seed.UserId.ToString());
            await SetLocalContextAsync(gated, "app.company_id", string.Empty);
            Assert.Equal(0L, await CountReadableCandidatesAsync(gated, seed.InboundMessageId));

            await SetLocalContextAsync(gated, "app.company_id", seed.CompanyId.ToString());
            Assert.Equal(1L, await CountReadableCandidatesAsync(gated, seed.InboundMessageId));

            // Empresa divergente do candidato sob o mesmo ator multiempresa:
            // o binding do GUC continua segurando mesmo com membership lá.
            await SetLocalContextAsync(
                gated,
                "app.company_id",
                Guid.NewGuid().ToString()
            );
            Assert.Equal(0L, await CountReadableCandidatesAsync(gated, seed.InboundMessageId));
        }
        finally
        {
            await gatedTransaction.RollbackAsync();
        }

        // A correção de segurança não é reversível: o downgrade recusa e a
        // migration permanece aplicada com o binding intacto.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(BeforeCandidateCompanyContext)
        );
        Assert.Equal(PostgresErrorCodes.FeatureNotSupported, exception.SqlState);

        await using var afterRefusal = await fixture.OpenAdministratorConnectionAsync();
        await using var refusalTransaction = await afterRefusal.BeginTransactionAsync();
        try
        {
            await SetLocalContextAsync(afterRefusal, "app.user_id", seed.UserId.ToString());
            await SetLocalContextAsync(afterRefusal, "app.company_id", string.Empty);
            Assert.Equal(0L, await CountReadableCandidatesAsync(afterRefusal, seed.InboundMessageId));
        }
        finally
        {
            await refusalTransaction.RollbackAsync();
        }
    }

    private static async Task SetLocalContextAsync(
        NpgsqlConnection connection,
        string name,
        string value
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config(@name, @value, true)",
            connection
        );
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountReadableCandidatesAsync(
        NpgsqlConnection connection,
        Guid inboundMessageId
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.read_inbound_whatsapp_invoice_candidate(@inbound_id)",
            connection
        );
        command.Parameters.AddWithValue("inbound_id", inboundMessageId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record InvoiceCandidateSeed(Guid UserId, Guid CompanyId, Guid InboundMessageId);

    private static async Task<InvoiceCandidateSeed> SeedInvoiceCandidateAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actor = new User(Guid.NewGuid(), "Ator do candidato", "paguai-candidate@example.test");
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo do candidato", actor.Id, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Empresa do candidato",
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

        var inboundId = Guid.NewGuid();
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var seed = new NpgsqlCommand(
            """
            INSERT INTO notifications.inbound_whatsapp_messages (
                id, phone_number_id, message_id, wa_id, message_type, media_id, status,
                resolution_state, resolved_user_id, resolved_company_id, resolved_membership_id, resolved_at)
            VALUES (@id, '111111111111111', @message_id, '556198132901', 'document', @media_id, 'completed',
                'admitted', @user_id, @company_id, @membership_id, clock_timestamp());
            INSERT INTO notifications.inbound_whatsapp_documents (
                inbound_message_id, company_id, user_id, membership_id, mime_type, size_bytes,
                checksum, scan_status, scanned_at, provider_key)
            VALUES (@id, @company_id, @user_id, @membership_id, 'application/pdf', 100,
                'sha256:' || repeat('a', 64), 'clean', clock_timestamp(), 'quarantine/whatsapp/test');
            INSERT INTO notifications.inbound_whatsapp_invoice_candidates (
                inbound_message_id, company_id, user_id, state,
                beneficiary_name, amount, currency, due_date, document_number, needs_review_reason)
            VALUES (@id, @company_id, @user_id, 'candidate',
                'ACME LTDA', '250.00', 'BRL', DATE '2026-09-05', 'NF 123', NULL)
            """,
            connection
        );
        seed.Parameters.AddWithValue("id", inboundId);
        seed.Parameters.AddWithValue("message_id", $"wamid.doc.{Guid.NewGuid():N}");
        seed.Parameters.AddWithValue("media_id", $"media.{Guid.NewGuid():N}");
        seed.Parameters.AddWithValue("user_id", actor.Id);
        seed.Parameters.AddWithValue("company_id", company.Id);
        seed.Parameters.AddWithValue("membership_id", membership.Id);
        await seed.ExecuteNonQueryAsync();

        return new InvoiceCandidateSeed(actor.Id, company.Id, inboundId);
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
