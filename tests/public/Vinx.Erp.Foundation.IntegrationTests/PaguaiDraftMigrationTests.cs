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
    private const string BeforeProofFileMigration = "20260827121400_AddTelegramAgentAdapter";
    private const string ProofFileMigration = "20260828032135_AddPaguaiRequestProofFile";

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

    [Fact]
    public async Task Empty_downgrade_keeps_company_context_binding_for_candidates_inserted_later()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(CandidateCompanyContextMigration);

        await migrator.Database.MigrateAsync(BeforeCandidateCompanyContext);
        var seed = await SeedCrossCompanyInvoiceCandidateAsync(fixture);

        await using var gated = await fixture.OpenAdministratorConnectionAsync();
        await using var transaction = await gated.BeginTransactionAsync();
        try
        {
            await SetLocalContextAsync(gated, "app.user_id", seed.UserId.ToString());
            await SetLocalContextAsync(gated, "app.company_id", seed.SelectedCompanyId.ToString());

            Assert.Equal(0L, await CountReadableCandidatesAsync(gated, seed.InboundMessageId));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Proof_column_grant_is_column_level_only_for_runtime()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(ProofFileMigration);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                has_column_privilege('vinx_runtime', 'paguai.requests', 'proof_file_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'paguai.requests', 'version', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'paguai.requests', 'amount', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'paguai.requests', 'beneficiary_display_name', 'UPDATE')
            """,
            administrator
        );
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        // A troca versionada (UPDATE em `version`) chega no PR empilhado de CAS;
        // este corte concede apenas o ponteiro do comprovante.
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Proof_downgrade_fails_closed_while_a_proof_is_bound()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(ProofFileMigration);
        var bound = await SeedProofBoundDraftAsync(fixture, bindProof: true);

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeProofFileMigration)
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM "__EFMigrationsHistory"
                    WHERE migration_id = @migration_id),
                EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'paguai'
                      AND table_name = 'requests'
                      AND column_name = 'proof_file_id'),
                EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = 'paguai'
                      AND indexname = 'uq_paguai_requests_proof_file'),
                (SELECT count(*) FROM paguai.requests WHERE id = @bound_id)
            """,
            administrator
        );
        verify.Parameters.AddWithValue("migration_id", ProofFileMigration);
        verify.Parameters.AddWithValue("bound_id", bound);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task Empty_proof_downgrade_removes_column_and_keeps_the_fixed_version_check()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(ProofFileMigration);

        await migrator.Database.MigrateAsync(BeforeProofFileMigration);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'paguai'
                      AND table_name = 'requests'
                      AND column_name = 'proof_file_id'),
                COALESCE((
                    SELECT has_column_privilege('vinx_runtime', 'paguai.requests', attribute.attname, 'UPDATE')
                    FROM pg_attribute AS attribute
                    WHERE attribute.attrelid = 'paguai.requests'::regclass
                      AND attribute.attname = 'proof_file_id'
                      AND NOT attribute.attisdropped), false),
                has_column_privilege('vinx_runtime', 'paguai.requests', 'version', 'UPDATE'),
                pg_get_constraintdef(ck.oid)
            FROM pg_constraint AS ck
            INNER JOIN pg_class AS rel ON rel.oid = ck.conrelid
            INNER JOIN pg_namespace AS nsp ON nsp.oid = rel.relnamespace
            WHERE nsp.nspname = 'paguai' AND rel.relname = 'requests'
              AND ck.conname = 'ck_paguai_requests_version'
            """,
            administrator
        );
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.Contains("version = 1", reader.GetString(3), StringComparison.Ordinal);
        await reader.DisposeAsync();

        await using var rejectBump = new NpgsqlCommand(
            """
            INSERT INTO paguai.requests (
                id, company_id, requester_user_id, request_id, title, description,
                amount, currency, due_date, beneficiary_display_name, proof_requirement,
                state, version, created_at)
            VALUES (@id, @company_id, @requester, @request_id, 't', 'd',
                    1.00, 'BRL', '2026-09-01', 'Fornecedor Demo', 'required',
                    'rascunho', 2, clock_timestamp())
            """,
            administrator);
        SeedDraftParameters(rejectBump, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var refusal = await Record.ExceptionAsync(() => rejectBump.ExecuteNonQueryAsync());
        Assert.IsType<PostgresException>(refusal);
    }

    private static void SeedDraftParameters(
        NpgsqlCommand command, Guid id, Guid companyId, Guid requester, Guid requestId)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("requester", requester);
        command.Parameters.AddWithValue("request_id", requestId);
    }

    /// <summary>
    /// Insere um rascunho no estado histórico da migration de prova: quando
    /// <paramref name="bindProof"/> cria arquivo confirmado para satisfazer a
    /// FK composta e o vincula.
    /// </summary>
    private static async Task<Guid> SeedProofBoundDraftAsync(
        FoundationPostgresFixture fixture, bool bindProof)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actor = new User(
            Guid.NewGuid(), "Ator da prova", $"paguai-proof-{Guid.NewGuid():N}@example.test");
        var requestId = Guid.NewGuid();
        // Nomes únicos por chamada: o mesmo teste semeia dois rascunhos e
        // groups.name/companies têm índice único.
        var discriminator = Guid.NewGuid().ToString("N")[..8];
        var group = new Group(Guid.NewGuid(), $"Grupo da prova {discriminator}", actor.Id, requestId);
        var company = new Company(
            Guid.NewGuid(), group.Id, $"Empresa da prova {discriminator}", actor.Id, requestId);
        var membership = new Membership(Guid.NewGuid(), company.Id, actor.Id, actor.Id, requestId);
        administrator.AddRange(actor, group, company, membership);
        var proof = new FileRecord(
            Guid.NewGuid(),
            company.Id,
            membership.Id,
            "comprovante.pdf",
            ".pdf",
            10,
            "application/pdf",
            new string('b', 64),
            "paguai_request_proof",
            "integration-test",
            DateTimeOffset.UtcNow.AddDays(1),
            actor.Id,
            requestId);
        proof.MarkVerified(actor.Id, requestId);
        proof.MarkConfirmed(actor.Id, requestId);
        administrator.Files.Add(proof);
        await administrator.SaveChangesAsync();

        var draftId = Guid.NewGuid();
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO paguai.requests (
                id, company_id, requester_user_id, request_id, title, description,
                amount, currency, due_date, beneficiary_display_name, proof_requirement,
                proof_file_id, state, version, created_at)
            VALUES (@id, @company_id, @requester, @request_id, 't', 'd',
                    1.00, 'BRL', '2026-09-01', 'Fornecedor Demo', 'required',
                    @proof_file_id, 'rascunho', 1, clock_timestamp())
            """,
            connection);
        SeedDraftParameters(command, draftId, company.Id, actor.Id, Guid.NewGuid());
        command.Parameters.AddWithValue("proof_file_id", bindProof ? proof.Id : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return draftId;
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

    private sealed record CrossCompanyInvoiceCandidateSeed(
        Guid UserId,
        Guid SelectedCompanyId,
        Guid InboundMessageId
    );

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

    private static async Task<CrossCompanyInvoiceCandidateSeed> SeedCrossCompanyInvoiceCandidateAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actor = new User(
            Guid.NewGuid(),
            "Ator multiempresa",
            "paguai-cross-company@example.test"
        );
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo multiempresa", actor.Id, requestId);
        var selectedCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Empresa selecionada",
            actor.Id,
            requestId
        );
        var candidateCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Empresa do candidato",
            actor.Id,
            requestId
        );
        var selectedMembership = new Membership(
            Guid.NewGuid(),
            selectedCompany.Id,
            actor.Id,
            actor.Id,
            requestId
        );
        var candidateMembership = new Membership(
            Guid.NewGuid(),
            candidateCompany.Id,
            actor.Id,
            actor.Id,
            requestId
        );
        administrator.AddRange(
            actor,
            group,
            selectedCompany,
            candidateCompany,
            selectedMembership,
            candidateMembership
        );
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
        seed.Parameters.AddWithValue("company_id", candidateCompany.Id);
        seed.Parameters.AddWithValue("membership_id", candidateMembership.Id);
        await seed.ExecuteNonQueryAsync();

        return new CrossCompanyInvoiceCandidateSeed(
            actor.Id,
            selectedCompany.Id,
            inboundId
        );
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
        // A coluna source_proposal_id só existe a partir de 20260827121200;
        // o fixture migra apenas até 20260826150000, então o insert é SQL cru
        // com o conjunto de colunas disponível nesse estado histórico.
        await using var insertConnection = await fixture.OpenAdministratorConnectionAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO paguai.requests (
                id, company_id, requester_user_id, request_id, title, description,
                amount, currency, due_date, beneficiary_display_name,
                proof_requirement, invoice_file_id, source_inbound_message_id,
                state, version, created_at)
            VALUES (
                @id, @company_id, @requester_user_id, @request_id, @title,
                @description, @amount, @currency, @due_date,
                @beneficiary_display_name, @proof_requirement, @invoice_file_id,
                NULL, @state, @version, clock_timestamp())
            """,
            insertConnection);
        insert.Parameters.AddWithValue("id", draft.Id);
        insert.Parameters.AddWithValue("company_id", draft.CompanyId);
        insert.Parameters.AddWithValue("requester_user_id", draft.RequesterUserId);
        insert.Parameters.AddWithValue("request_id", draft.RequestId);
        insert.Parameters.AddWithValue("title", draft.Title);
        insert.Parameters.AddWithValue("description", draft.Description);
        insert.Parameters.AddWithValue("amount", draft.Amount);
        insert.Parameters.AddWithValue("currency", draft.Currency);
        insert.Parameters.AddWithValue("due_date", draft.DueDate);
        insert.Parameters.AddWithValue(
            "beneficiary_display_name",
            draft.BeneficiaryDisplayName);
        insert.Parameters.AddWithValue("proof_requirement", draft.ProofRequirement);
        insert.Parameters.AddWithValue("invoice_file_id", draft.InvoiceFileId!.Value);
        insert.Parameters.AddWithValue("state", draft.State);
        insert.Parameters.AddWithValue("version", draft.Version);
        await insert.ExecuteNonQueryAsync();
        return draft;
    }
}
