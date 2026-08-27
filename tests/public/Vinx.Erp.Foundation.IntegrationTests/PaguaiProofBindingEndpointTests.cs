using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Fix P1: o comprovante enviado no diálogo deve persistir vinculado ao
/// rascunho (coluna paguai.requests.proof_file_id), com validação pela
/// fronteira pública de arquivos Foundation (existência, empresa, autor do
/// upload e estado pós-quarentena), troca versionada com auditoria no mesmo
/// commit e link de leitura de curta duração emitido sob autorização fresca.
/// </summary>
public sealed class PaguaiProofBindingEndpointTests
{
    private static readonly DateTimeOffset Retention =
        DateTimeOffset.UtcNow.AddDays(30);

    [Fact]
    public async Task Draft_created_with_proof_persists_binding_and_returns_metadata_after_fresh_read()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "comprovante.pdf");
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, proof.Id)
        );

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(proof.Id, ReadGuid(createdDocument.RootElement, "proofFileId"));
        Assert.Equal("comprovante.pdf", createdDocument.RootElement.GetProperty("proofFileName").GetString());

        using var fresh = await client.GetAsync(
            $"/paguai/requests/{ReadGuid(createdDocument.RootElement, "id")}?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var freshDocument = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        Assert.Equal(proof.Id, ReadGuid(freshDocument.RootElement, "proofFileId"));
        Assert.Equal("comprovante.pdf", freshDocument.RootElement.GetProperty("proofFileName").GetString());
        Assert.Equal("application/pdf", freshDocument.RootElement.GetProperty("proofFileMimeType").GetString());
        Assert.Equal(10, freshDocument.RootElement.GetProperty("proofFileSizeBytes").GetInt64());
    }

    [Fact]
    public async Task Create_with_cross_company_file_fails_closed_as_not_found()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var foreignProof = await SeedConfirmedFileAsync(
            factory,
            scenario.ForeignCompanyId,
            scenario.ForeignMembershipId,
            "outro-tenant.pdf");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, foreignProof.Id)
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"proof_file_not_found\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Create_with_file_from_another_uploader_fails_closed_as_not_found()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var foreignOwned = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.OtherUploaderMembershipId,
            "alheio.pdf");

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, foreignOwned.Id)
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"proof_file_not_found\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Already_bound_file_rejected_with_typed_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "comprovante.pdf");
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, proof.Id)
        );
        using var second = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, proof.Id)
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            "{\"code\":\"proof_file_bound\"}",
            await second.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Unique_partial_index_makes_double_bind_impossible_at_storage_layer()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var seeded = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "unico.pdf");
        var proofId = seeded.Id;
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        const string insertSql = """
            INSERT INTO paguai.requests (
                id, company_id, requester_user_id, request_id, title, description,
                amount, currency, due_date, beneficiary_display_name, proof_requirement,
                proof_file_id, state, version, created_at)
            VALUES (@id, @company_id, @requester, @request_id, 't', 'd',
                    1.00, 'BRL', '2026-09-01', 'Fornecedor Demo', 'required',
                    @proof_file_id, 'rascunho', 1, clock_timestamp())
            """;
        async Task InsertRowAsync(Guid rowId, Guid? boundFile)
        {
            await using var command = new NpgsqlCommand(insertSql, connection);
            command.Parameters.AddWithValue("id", rowId);
            command.Parameters.AddWithValue("company_id", scenario.CompanyId);
            command.Parameters.AddWithValue("requester", factory.ActorUserId);
            command.Parameters.AddWithValue("request_id", Guid.NewGuid());
            command.Parameters.AddWithValue("proof_file_id", boundFile ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        await InsertRowAsync(Guid.NewGuid(), proofId);
        var conflict = await Record.ExceptionAsync(() => InsertRowAsync(Guid.NewGuid(), proofId));

        var postgresConflict = Assert.IsType<PostgresException>(conflict);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresConflict.SqlState);
        Assert.Equal("uq_paguai_requests_proof_file", postgresConflict.ConstraintName);
    }

    [Fact]
    public async Task Replace_proof_binds_new_file_bumps_version_and_audits_swap_in_same_transaction()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var original = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "original.pdf");
        var replacement = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "substituto.pdf");
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, original.Id)
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createdDocument.RootElement, "id");

        using var swapped = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new
            {
                companyId = scenario.CompanyId,
                expectedVersion = 1,
                proofFileId = replacement.Id,
            }
        );

        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        using var swappedDocument = JsonDocument.Parse(await swapped.Content.ReadAsStringAsync());
        Assert.Equal(replacement.Id, ReadGuid(swappedDocument.RootElement, "proofFileId"));
        Assert.Equal(2, swappedDocument.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("substituto.pdf", swappedDocument.RootElement.GetProperty("proofFileName").GetString());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var audits = await administrator.AuditEvents.Where(auditEvent =>
                auditEvent.Action == "paguai.draft.replace_proof"
                && auditEvent.ResourceType == "paguai_request"
                && auditEvent.ResourceId == draftId)
            .ToListAsync();
        var auditEvent = Assert.Single(audits);
        Assert.Equal(AuditOutcome.Success, auditEvent.Outcome);

        // O arquivo recém-vinculado segue único: nenhum outro rascunho o recebe.
        var rebound = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, replacement.Id)
        );
        Assert.Equal(HttpStatusCode.Conflict, rebound.StatusCode);
    }

    [Fact]
    public async Task Replace_proof_with_stale_expected_version_returns_typed_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var first = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "primeiro.pdf");
        var second = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "segundo.pdf");
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, first.Id)
        );
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createdDocument.RootElement, "id");

        using var stale = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, expectedVersion = 9, proofFileId = second.Id }
        );

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "{\"code\":\"proof_version_conflict\"}",
            await stale.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Proof_link_issues_short_lived_url_under_fresh_authorization()
    {
        var storage = new StubProofLinkStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "comprovante.pdf");
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, proof.Id)
        );
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createdDocument.RootElement, "id");

        storage.ResetAttempts();
        using var link = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}"
        );

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        using var linkDocument = JsonDocument.Parse(await link.Content.ReadAsStringAsync());
        var url = Assert.IsType<string>(linkDocument.RootElement.GetProperty("url").GetString());
        Assert.StartsWith("https://signed.storage.test/", url, StringComparison.Ordinal);
        Assert.True(linkDocument.RootElement.TryGetProperty("expiresAt", out var expiresAt));
        Assert.True(DateTimeOffset.TryParse(expiresAt.GetString(), out _));
        var attempt = Assert.Single(storage.AttemptsSnapshot);
        Assert.Contains($"{scenario.CompanyId:N}", attempt.ProviderKey, StringComparison.Ordinal);
        Assert.Contains($"{proof.Id:N}", attempt.ProviderKey, StringComparison.Ordinal);
        // Pin exato do TTL do produtor de links.
        Assert.Equal(TimeSpan.FromMinutes(5), attempt.Lifetime);
    }

    [Fact]
    public async Task Proof_link_fails_closed_without_read_grant_or_cross_company_scope()
    {
        var storage = new StubProofLinkStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "comprovante.pdf");
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests",
            ProofRequest(scenario.CompanyId, proof.Id)
        );
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createdDocument.RootElement, "id");

        // Revogação fresca do grant de leitura antes do novo pedido de link.
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.RoleCapabilities.Where(grant =>
                    grant.CompanyId == scenario.CompanyId
                    && grant.CapabilityId == KnownCapabilities.PaguaiDraftReadId)
                .ExecuteDeleteAsync();
            await administrator.SaveChangesAsync();
        }

        using var revoked = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Equal(
            "{\"code\":\"resource_not_found\"}",
            await revoked.Content.ReadAsStringAsync()
        );

        // Empresa estrangeira não expõe o rascunho nem emite link.
        using var foreign = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.ForeignCompanyId}"
        );
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(storage.AttemptsSnapshot);
    }

    [Fact]
    public async Task Proof_link_on_unconfirmed_file_returns_typed_state_error()
    {
        var storage = new StubProofLinkStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var quarantined = await SeedQuarantinedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "em-quarentena.pdf");
        // Rascunho já vinculado a arquivo em quarentena para exercitar apenas o
        // caminho de leitura do link sem passar pelo bind validado.
        var draftId = Guid.NewGuid();
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.PaguaiRequests.Add(
                new PaguaiRequest
                {
                    Id = draftId,
                    CompanyId = scenario.CompanyId,
                    RequesterUserId = factory.ActorUserId,
                    RequestId = Guid.NewGuid(),
                    Title = "Comprovante pendente",
                    Description = "Rascunho com quarentena aberta",
                    Amount = 10.00m,
                    Currency = "BRL",
                    DueDate = new DateOnly(2026, 10, 1),
                    BeneficiaryDisplayName = "Fornecedor Demo",
                    ProofRequirement = "optional",
                    ProofFileId = quarantined.Id,
                    State = "rascunho",
                    Version = 1,
                });
            await administrator.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var link = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}"
        );

        Assert.Equal(HttpStatusCode.BadRequest, link.StatusCode);
        Assert.Equal(
            "{\"code\":\"proof_file_not_ready\"}",
            await link.Content.ReadAsStringAsync()
        );
        Assert.Empty(storage.AttemptsSnapshot);
    }

    [Fact]
    public async Task Replay_of_identical_proof_payload_replays_same_draft()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedConfirmedFileAsync(
            factory,
            scenario.CompanyId,
            scenario.MembershipId,
            "comprovante.pdf");
        using var client = factory.CreateClient();
        var request = ProofRequest(scenario.CompanyId, proof.Id);

        using var first = await client.PostAsJsonAsync("/paguai/requests", request);
        using var replay = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            ReadGuid(firstDocument.RootElement, "id"),
            ReadGuid(replayDocument.RootElement, "id")
        );
        Assert.Equal(
            ReadGuid(firstDocument.RootElement, "proofFileId"),
            ReadGuid(replayDocument.RootElement, "proofFileId")
        );
    }

    private static ProofRequestBody ProofRequest(Guid companyId, Guid fileId) =>
        new(
            companyId,
            Guid.NewGuid(),
            "Office supplies",
            "Replacement printer paper",
            "1250.00",
            "BRL",
            new DateOnly(2026, 9, 1),
            "Fornecedor Demo",
            "required",
            fileId);

    private static Task<(Guid Id, string ProviderKey)> SeedConfirmedFileAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid uploaderMembershipId,
        string fileName) =>
        SeedFileAsync(factory, companyId, uploaderMembershipId, fileName, confirmed: true);

    private static Task<(Guid Id, string ProviderKey)> SeedQuarantinedFileAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid uploaderMembershipId,
        string fileName) =>
        SeedFileAsync(factory, companyId, uploaderMembershipId, fileName, confirmed: false);

    private static async Task<(Guid Id, string ProviderKey)> SeedFileAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid uploaderMembershipId,
        string fileName,
        bool confirmed)
    {
        var fileId = Guid.NewGuid();
        var lifecycleRequestId = Guid.NewGuid();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var checksum = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName))).ToLowerInvariant()}";
        var file = new FileRecord(
            fileId,
            companyId,
            uploaderMembershipId,
            fileName,
            Path.GetExtension(fileName).TrimStart('.'),
            10,
            "application/pdf",
            checksum,
            "paguai_request_proof",
            "web-paguai-draft",
            Retention,
            factory.ActorUserId,
            lifecycleRequestId);
        if (confirmed)
        {
            file.MarkVerified(factory.ActorUserId, lifecycleRequestId);
            file.MarkConfirmed(factory.ActorUserId, lifecycleRequestId);
        }

        administrator.Files.Add(file);
        await administrator.SaveChangesAsync();
        return (fileId, file.ProviderKey);
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        Assert.True(root.TryGetProperty(propertyName, out var property));
        Assert.True(Guid.TryParse(property.GetString(), out var value));
        return value;
    }

    private static async Task<ProofScenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Obras",
            factory.ActorUserId,
            requestId
        );
        var foreignCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Serviços",
            factory.ActorUserId,
            requestId
        );
        var membership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var foreignMembership = new Membership(
            Guid.NewGuid(),
            foreignCompany.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var otherUserId = Guid.NewGuid();
        var otherUser = new User(otherUserId, "Outro Autor", $"other-{otherUserId:N}@vinx.test");
        var otherMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            otherUserId,
            factory.ActorUserId,
            requestId
        );
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Paguai requester",
            factory.ActorUserId,
            requestId
        );
        // Catálogo já semeado pela migration: somente adicionar quando ausente.
        var readCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == "paguai.draft.read"
        );
        if (readCapability is null)
        {
            readCapability = new Capability(
                Guid.Parse("766bdb56-80c0-5dee-982f-be7f0d3eaea8"),
                "paguai.draft.read");
            administrator.Capabilities.Add(readCapability);
        }
        var createCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == "paguai.draft.create"
        );
        if (createCapability is null)
        {
            createCapability = new Capability(
                Guid.Parse("aecaa296-6a6f-5815-93ac-bd1ccdff3f44"),
                "paguai.draft.create");
            administrator.Capabilities.Add(createCapability);
        }

        var grants = new[]
        {
            new RoleCapability(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                readCapability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId),
            new RoleCapability(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                createCapability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId),
        };
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            membership.Id,
            factory.ActorUserId,
            requestId);

        administrator.AddRange(
            group,
            company,
            foreignCompany,
            otherUser,
            membership,
            foreignMembership,
            otherMembership,
            role,
            assignment);
        administrator.AddRange(grants);
        await administrator.SaveChangesAsync();

        return new ProofScenario(
            company.Id,
            foreignCompany.Id,
            membership.Id,
            foreignMembership.Id,
            otherMembership.Id);
    }

    private sealed record ProofRequestBody(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description,
        string Amount,
        string Currency,
        DateOnly DueDate,
        string BeneficiaryDisplayName,
        string ProofRequirement,
        Guid? ProofFileId);

    private sealed record ProofScenario(
        Guid CompanyId,
        Guid ForeignCompanyId,
        Guid MembershipId,
        Guid ForeignMembershipId,
        Guid OtherUploaderMembershipId);

    /// <summary>Dobra de FileStorage que só sabe assinar URLs curtas; registra as tentativas.</summary>
    private sealed class StubProofLinkStorage : FileStorage
    {
        private readonly List<(string ProviderKey, TimeSpan Lifetime)> _attempts = [];

        public IReadOnlyList<(string ProviderKey, TimeSpan Lifetime)> AttemptsSnapshot => [.. _attempts];

        public void ResetAttempts() => _attempts.Clear();

        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
            string providerKey,
            TimeSpan lifetime,
            CancellationToken cancellationToken)
        {
            _attempts.Add((providerKey, lifetime));
            return Task.FromResult(
                new SignedStorageUrl(
                    new Uri($"https://signed.storage.test/{providerKey}"),
                    DateTimeOffset.UtcNow.Add(lifetime)));
        }

        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
            string providerKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadObjectAsync(
            string providerKey,
            ReadOnlyMemory<byte> content,
            string contentType,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageObjectMetadata> GetObjectMetadataAsync(
            string providerKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageObjectInspection> InspectObjectAsync(
            string providerKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<System.IO.Stream> OpenReadAsync(
            string providerKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
            string prefix,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
