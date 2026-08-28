using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// MVP #345 (contrato congelado): comprovante é documentação anexada a um
/// rascunho — sem estado financeiro, evento próprio ou notificação. Criação com
/// prova opcional, troca CAS auditada no mesmo commit, leitura apenas de
/// metadados seguros e link curto revalidado contra a retenção do arquivo
/// imediatamente antes de cada assinatura.
/// </summary>
public sealed class PaguaiProofEndpointTests
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private static readonly DateTimeOffset Retention = DateTimeOffset.UtcNow.AddDays(30);

    [Fact]
    public async Task Create_with_proof_returns_created_draft_with_safe_metadata_and_single_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "comprovante.pdf");
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, proof.Id));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var draftId = ReadGuid(root, "id");
        Assert.Equal(proof.Id, ReadGuid(root, "proofFileId"));
        Assert.Equal("comprovante.pdf", root.GetProperty("proofFileName").GetString());
        Assert.Equal("application/pdf", root.GetProperty("proofFileMimeType").GetString());
        Assert.Equal(10, root.GetProperty("proofFileSizeBytes").GetInt64());
        Assert.Equal("rascunho", root.GetProperty("state").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        using var fresh = await client.GetAsync($"/paguai/requests/{draftId}?companyId={scenario.CompanyId}");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var freshBody = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        Assert.Equal(proof.Id, ReadGuid(freshBody.RootElement, "proofFileId"));
        Assert.Equal("comprovante.pdf", freshBody.RootElement.GetProperty("proofFileName").GetString());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal("rascunho", stored.State);
        Assert.Equal(proof.Id, stored.ProofFileId);
        var createAudit = Assert.Single(await administrator.AuditEvents.Where(audit =>
                audit.Action == "paguai.draft.create" && audit.ResourceId == draftId).ToListAsync());
        Assert.Equal("proof_attached", createAudit.Reason);
        // Nada além do evento padrão de rascunho criado: prova não gera evento próprio.
        var envelopes = await PaguaiDraftCreatedTests.ReadPaguaiQueueAsync(factory);
        Assert.All(envelopes, envelope => Assert.Equal(draftId, envelope.DraftId));
    }

    [Fact]
    public async Task Requirement_and_attachment_remain_independent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "opcional.pdf");
        using var client = factory.CreateClient();

        using var requiredWithoutProof = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, null, proofRequirement: "required"));
        using var optionalWithProof = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, proof.Id, proofRequirement: "optional"));

        Assert.Equal(HttpStatusCode.Created, requiredWithoutProof.StatusCode);
        Assert.Equal(HttpStatusCode.Created, optionalWithProof.StatusCode);
        using var requiredBody = JsonDocument.Parse(await requiredWithoutProof.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, requiredBody.RootElement.GetProperty("proofFileId").ValueKind);
        Assert.Equal("required", requiredBody.RootElement.GetProperty("proofRequirement").GetString());
    }

    [Fact]
    public async Task Patch_attaches_and_replaces_proof_with_version_bump_and_transactional_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var first = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "primeiro.pdf");
        var second = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "substituto.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proofRequirement: "required");

        using var attach = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = first.Id, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        using var attachBody = JsonDocument.Parse(await attach.Content.ReadAsStringAsync());
        Assert.Equal(first.Id, ReadGuid(attachBody.RootElement, "proofFileId"));
        Assert.Equal("primeiro.pdf", attachBody.RootElement.GetProperty("proofFileName").GetString());
        Assert.Equal(2, attachBody.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("rascunho", attachBody.RootElement.GetProperty("state").GetString());

        using var replace = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = second.Id, expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        using var replaceBody = JsonDocument.Parse(await replace.Content.ReadAsStringAsync());
        Assert.Equal(second.Id, ReadGuid(replaceBody.RootElement, "proofFileId"));
        Assert.Equal("substituto.pdf", replaceBody.RootElement.GetProperty("proofFileName").GetString());
        Assert.Equal(3, replaceBody.RootElement.GetProperty("version").GetInt32());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var audits = await administrator.AuditEvents.Where(audit =>
                audit.Action == "paguai.draft.replace_proof" && audit.ResourceId == draftId).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, audit => audit.Reason == "proof_attached" && audit.Outcome == AuditOutcome.Success);
        Assert.Contains(audits, audit => audit.Reason == "proof_replaced" && audit.Outcome == AuditOutcome.Success);
        // O arquivo substituído não é apagado nem tem a retenção alterada.
        Assert.True(await administrator.Files.AnyAsync(file => file.Id == first.Id && file.DeletedAt == null));
        var envelopes = await PaguaiDraftCreatedTests.ReadPaguaiQueueAsync(factory);
        Assert.All(envelopes, envelope => Assert.Equal(draftId, envelope.DraftId));
    }

    [Fact]
    public async Task Detail_and_list_expose_only_safe_proof_metadata()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "seguro.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        using var detail = await client.GetAsync($"/paguai/requests/{draftId}?companyId={scenario.CompanyId}");
        var detailJson = await detail.Content.ReadAsStringAsync();
        using var list = await client.GetAsync($"/paguai/requests?companyId={scenario.CompanyId}");
        var listJson = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(proof.ProviderKey, detailJson, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Checksum, detailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("retention", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", detailJson, StringComparison.OrdinalIgnoreCase);
        using var listBody = JsonDocument.Parse(listJson);
        var listed = listBody.RootElement.EnumerateArray().Single(item => ReadGuid(item, "id") == draftId);
        Assert.Equal(proof.Id, ReadGuid(listed, "proofFileId"));
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("proofFileName").ValueKind);
    }

    [Fact]
    public async Task Proof_link_is_freshly_signed_no_store_and_expires_within_five_minutes()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "comprovante.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        using var link = await client.GetAsync($"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var cacheControl = link.Headers.CacheControl?.ToString();
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        using var linkBody = JsonDocument.Parse(await link.Content.ReadAsStringAsync());
        var url = linkBody.RootElement.GetProperty("url").GetString();
        Assert.StartsWith("https://signed.storage.test/", url, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(linkBody.RootElement.GetProperty("expiresAt").GetString(), out _));
        var attempt = Assert.Single(storage.Attempts);
        Assert.Equal(TimeSpan.FromMinutes(5), attempt.Lifetime);
        Assert.Equal(proof.ProviderKey, attempt.ProviderKey);
    }

    [Fact]
    public async Task Proof_link_ttl_is_capped_by_retention_deadline()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var retention = DateTimeOffset.UtcNow.AddMinutes(2);
        var proof = await SeedFileAsync(
            factory, scenario.CompanyId, scenario.MembershipId, "curto.pdf", retentionUntil: retention);
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        using var link = await client.GetAsync($"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        using var linkBody = JsonDocument.Parse(await link.Content.ReadAsStringAsync());
        var expiresAt = DateTimeOffset.Parse(linkBody.RootElement.GetProperty("expiresAt").GetString()!);
        Assert.True(expiresAt <= retention, $"link {expiresAt:o} must not outlive retention {retention:o}");
        var attempt = Assert.Single(storage.Attempts);
        Assert.True(attempt.Lifetime > TimeSpan.FromMinutes(1), $"capped lifetime {attempt.Lifetime} too small");
        Assert.True(attempt.Lifetime < TimeSpan.FromMinutes(5), $"capped lifetime {attempt.Lifetime} not capped");
    }

    [Theory]
    [InlineData("empty_proof_file_id")]
    [InlineData("missing_company_id")]
    [InlineData("unknown_member")]
    public async Task Invalid_create_bodies_are_rejected_without_mutation_or_audit(string variation)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "corpo.pdf");
        using var client = factory.CreateClient();
        var payload = new Dictionary<string, object?>
        {
            ["companyId"] = scenario.CompanyId,
            ["requestId"] = Guid.NewGuid(),
            ["title"] = "Office supplies",
            ["description"] = "Replacement printer paper",
            ["amount"] = "1250.00",
            ["currency"] = "BRL",
            ["dueDate"] = new DateOnly(2026, 9, 1),
            ["beneficiaryDisplayName"] = "Fornecedor Demo",
            ["proofRequirement"] = "required",
            ["proofFileId"] = proof.Id,
        };
        if (variation == "empty_proof_file_id")
        {
            payload["proofFileId"] = Guid.Empty;
        }
        else if (variation == "missing_company_id")
        {
            payload.Remove("companyId");
        }
        else if (variation == "unknown_member")
        {
            payload["extra"] = 1;
        }

        using var post = await client.PostAsJsonAsync("/paguai/requests", payload);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await post.Content.ReadAsStringAsync());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.PaguaiRequests.ToListAsync());
        // A admissão de sessão da identidade audita sucesso por si só; o corte
        // Paguai não pode ter produzido nenhuma auditoria de criação.
        Assert.Empty(await administrator.AuditEvents.Where(audit =>
            audit.Action == "paguai.draft.create").ToListAsync());
    }

    [Theory]
    [InlineData("empty_proof_file_id")]
    [InlineData("missing_company_id")]
    [InlineData("zero_version")]
    [InlineData("unknown_member")]
    public async Task Invalid_patch_bodies_are_rejected_without_mutation_or_audit(string variation)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "corpo.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId);
        object patchBody = variation switch
        {
            "empty_proof_file_id" => new { companyId = scenario.CompanyId, proofFileId = Guid.Empty, expectedVersion = 1 },
            "zero_version" => new { companyId = scenario.CompanyId, proofFileId = proof.Id, expectedVersion = 0 },
            "unknown_member" => new { companyId = scenario.CompanyId, proofFileId = proof.Id, expectedVersion = 1, extra = 1 },
            _ => new { proofFileId = proof.Id, expectedVersion = 1 },
        };

        using var patch = await client.PatchAsJsonAsync($"/paguai/requests/{draftId}/proof", patchBody);
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await patch.Content.ReadAsStringAsync());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal(1, stored.Version);
        Assert.Null(stored.ProofFileId);
        Assert.Empty(await administrator.AuditEvents.Where(audit =>
            audit.ResourceId == draftId
            && audit.Outcome == AuditOutcome.Success
            && audit.Action != "paguai.draft.create").ToListAsync());
    }

    [Fact]
    public async Task Link_refuses_when_remaining_retention_is_below_margin_plus_one_second()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "limite.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        // Retenção a ~1,5s: acima de zero, abaixo da margem de segurança + 1
        // segundo válido no provedor — o link falha fechado, não assina.
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.Files.Where(file => file.Id == proof.Id)
                .ExecuteUpdateAsync(setter => setter.SetProperty(
                    file => file.RetentionUntil, DateTimeOffset.UtcNow.AddMilliseconds(1500)));
            await administrator.SaveChangesAsync();
        }

        using var link = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal("{\"code\":\"resource_not_found\"}", await link.Content.ReadAsStringAsync());
        Assert.Empty(storage.Attempts);
    }

    [Theory]
    [InlineData("missing_file")]
    [InlineData("cross_company")]
    [InlineData("other_uploader")]
    [InlineData("wrong_classification")]
    [InlineData("deleted")]
    [InlineData("expired_retention")]
    [InlineData("quarantined")]
    [InlineData("quarantined_other_uploader")]
    [InlineData("quarantined_wrong_classification")]
    [InlineData("quarantined_expired_retention")]
    public async Task Bind_lifecycle_failures_are_typed_and_audited_without_file_metadata(string variation)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var fileId = variation switch
        {
            "missing_file" => Guid.NewGuid(),
            "cross_company" => (await SeedFileAsync(
                factory, scenario.ForeignCompanyId, scenario.ForeignMembershipId, "fora.pdf")).Id,
            "other_uploader" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.OtherMembershipId, "alheio.pdf")).Id,
            "wrong_classification" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.MembershipId, "fatura.pdf",
                classification: "paguai_request_invoice")).Id,
            "deleted" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.MembershipId, "apagado.pdf")).Id,
            "expired_retention" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.MembershipId, "vencido.pdf")).Id,
            "quarantined" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.MembershipId, "quarentena.pdf", confirmed: false)).Id,
            // Precedência: quarentena + defeito oculto permanece 404 — o
            // "não pronto" só existe para classe/autoria/vida corretas.
            "quarantined_other_uploader" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.OtherMembershipId,
                "quarentena-alheio.pdf", confirmed: false)).Id,
            "quarantined_wrong_classification" => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.MembershipId,
                "quarentena-fatura.pdf", classification: "paguai_request_invoice",
                confirmed: false)).Id,
            _ => (await SeedFileAsync(
                factory, scenario.CompanyId, scenario.MembershipId,
                "quarentena-vencido.pdf", confirmed: false)).Id,
        };

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            if (variation == "deleted")
            {
                (await administrator.Files.SingleAsync(file => file.Id == fileId))
                    .MarkDeleted("test_deletion", factory.ActorUserId, Guid.NewGuid());
            }
            else if (variation is "expired_retention" or "quarantined_expired_retention")
            {
                // O construtor de FileRecord rejeita retenção passada: expira
                // por update direto, mantendo o arquivo em quarentena.
                await administrator.Files.Where(file => file.Id == fileId)
                    .ExecuteUpdateAsync(setter => setter.SetProperty(
                        file => file.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }

            await administrator.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, fileId));

        var json = await response.Content.ReadAsStringAsync();
        if (variation == "quarantined")
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("{\"code\":\"proof_file_not_ready\"}", json);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("{\"code\":\"proof_file_not_found\"}", json);
        }

        await using var administrator2 = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(await administrator2.PaguaiRequests.AnyAsync());
        var denial = await administrator2.AuditEvents.SingleOrDefaultAsync(audit =>
            audit.Outcome == AuditOutcome.Denied
            && (audit.Action == "proof_file_not_found" || audit.Action == "proof_file_not_ready"));
        Assert.NotNull(denial);
        Assert.Equal("{}", denial.MetadataJson);
        Assert.DoesNotContain(fileId.ToString("N"), denial.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_lifecycle_fails_closed_after_expiry_or_deletion_or_without_proof()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var expired = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "vencido.pdf");
        var deleted = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "apagado.pdf");
        using var client = factory.CreateClient();
        var expiredDraft = await CreateDraftAsync(client, scenario.CompanyId, expired.Id);
        var deletedDraft = await CreateDraftAsync(client, scenario.CompanyId, deleted.Id);
        var bareDraft = await CreateDraftAsync(client, scenario.CompanyId);

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.Files.Where(file => file.Id == expired.Id)
                .ExecuteUpdateAsync(setter => setter.SetProperty(
                    file => file.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
            (await administrator.Files.SingleAsync(file => file.Id == deleted.Id))
                .MarkDeleted("test_deletion", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        foreach (var draftId in new[] { expiredDraft, deletedDraft, bareDraft })
        {
            using var link = await client.GetAsync(
                $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");
            Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
            Assert.Equal("{\"code\":\"resource_not_found\"}", await link.Content.ReadAsStringAsync());
        }

        Assert.Empty(storage.Attempts);
    }

    [Fact]
    public async Task Unauthenticated_calls_are_rejected()
    {
        // Identidade externa desconhecida: autenticação presente, identidade
        // inexistente — a fronteira responde 401 sem revelar nada do Paguai.
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(
            "https://project.supabase.co/auth/v1",
            "unknown-proof-subject");
        using var anonymous = factory.CreateClient();
        var companyId = Guid.NewGuid();

        using var create = await anonymous.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(companyId, null));
        using var patch = await anonymous.PatchAsJsonAsync(
            $"/paguai/requests/{Guid.NewGuid()}/proof",
            new { companyId, proofFileId = Guid.NewGuid(), expectedVersion = 1 });
        using var link = await anonymous.GetAsync(
            $"/paguai/requests/{Guid.NewGuid()}/proof-link?companyId={companyId}");
        using var detail = await anonymous.GetAsync(
            $"/paguai/requests/{Guid.NewGuid()}?companyId={companyId}");

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, link.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, detail.StatusCode);
    }

    [Fact]
    public async Task Capability_denials_are_typed_for_create_and_fail_closed_for_reads()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "capability.pdf");
        using var client = factory.CreateClient();
        // O rascunho nasce COM comprovante enquanto a capability existe; só
        // então as leituras são revogadas para provar o fail-closed do link.
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.RoleCapabilities.Where(grant =>
                    grant.CompanyId == scenario.CompanyId
                    && grant.CapabilityId == KnownCapabilities.PaguaiDraftCreateId)
                .ExecuteDeleteAsync();
            await administrator.SaveChangesAsync();
        }

        using var denied = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, proof.Id));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("{\"code\":\"capability_denied\"}", await denied.Content.ReadAsStringAsync());

        var replacement = await SeedFileAsync(
            factory, scenario.CompanyId, scenario.MembershipId, "substituto-capability.pdf");
        using var deniedPatch = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = replacement.Id, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, deniedPatch.StatusCode);
        Assert.Equal("{\"code\":\"capability_denied\"}", await deniedPatch.Content.ReadAsStringAsync());

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.RoleCapabilities.Where(grant =>
                    grant.CompanyId == scenario.CompanyId
                    && grant.CapabilityId == KnownCapabilities.PaguaiDraftReadId)
                .ExecuteDeleteAsync();
            await administrator.SaveChangesAsync();
        }

        using var detail = await client.GetAsync($"/paguai/requests/{draftId}?companyId={scenario.CompanyId}");
        using var link = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("{\"code\":\"resource_not_found\"}", await detail.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal("{\"code\":\"resource_not_found\"}", await link.Content.ReadAsStringAsync());
        // Assinador de teste registra cada chamada: nenhuma aconteceu.
        Assert.Empty(storage.Attempts);

        await using var administrator2 = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator2.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal(proof.Id, stored.ProofFileId);
        Assert.Equal(1, stored.Version);
        Assert.NotNull(await administrator2.AuditEvents.SingleOrDefaultAsync(audit =>
            audit.Action == "capability_denied" && audit.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task Foreign_requester_drafts_are_hidden_from_replace_read_and_link_with_durable_denial_audit()
    {
        var storage = new StubProofStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var foreignProof = await SeedFileAsync(
            factory, scenario.CompanyId, scenario.OtherMembershipId, "outro-autor.pdf");
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "meu.pdf");
        using var client = factory.CreateClient();
        var draftId = await SeedForeignDraftAsync(factory, scenario, foreignProof.Id);

        using var patch = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = proof.Id, expectedVersion = 1 });
        using var detail = await client.GetAsync($"/paguai/requests/{draftId}?companyId={scenario.CompanyId}");
        using var link = await client.GetAsync($"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");
        // Empresa em que o ator tem membership mas sem grants: negação de
        // capability (403) sem revelar existência de prova; o rascunho alheio
        // permanece oculto (404) na própria empresa.
        using var foreignCompanyPatch = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.ForeignCompanyId, proofFileId = proof.Id, expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal("{\"code\":\"resource_not_found\"}", await patch.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignCompanyPatch.StatusCode);
        Assert.Equal(
            "{\"code\":\"capability_denied\"}",
            await foreignCompanyPatch.Content.ReadAsStringAsync());
        Assert.Empty(storage.Attempts);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal(foreignProof.Id, stored.ProofFileId);
        Assert.Equal(1, stored.Version);
        // Toda tentativa autenticada de troca deixa negação durable com razão
        // controlada — sem id de comprovante nem metadados.
        var denial = await administrator.AuditEvents.SingleAsync(audit =>
            audit.Action == "resource_not_found" && audit.Outcome == AuditOutcome.Denied);
        Assert.Equal("resource_not_found", denial.Reason);
        Assert.Equal("{}", denial.MetadataJson);
        Assert.DoesNotContain(proof.Id.ToString("N"), denial.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_after_expiry_returns_original_draft_without_second_bind_or_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "replay.pdf");
        using var client = factory.CreateClient();
        var request = ProofRequest(scenario.CompanyId, proof.Id);

        using var first = await client.PostAsJsonAsync("/paguai/requests", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var draftId = ReadGuid(JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement, "id");

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.Files.Where(file => file.Id == proof.Id)
                .ExecuteUpdateAsync(setter => setter.SetProperty(
                    file => file.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await administrator.SaveChangesAsync();
        }

        using var replay = await client.PostAsJsonAsync("/paguai/requests", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(draftId, ReadGuid(replayBody.RootElement, "id"));
        // O replay devolve o rascunho com o ponteiro, mas SEM metadados stale:
        // o enriquecimento é sempre fresco e o arquivo já venceu.
        Assert.Equal(proof.Id, ReadGuid(replayBody.RootElement, "proofFileId"));
        Assert.Equal(JsonValueKind.Null, replayBody.RootElement.GetProperty("proofFileName").ValueKind);
        Assert.Equal(JsonValueKind.Null, replayBody.RootElement.GetProperty("proofFileMimeType").ValueKind);
        Assert.Equal(JsonValueKind.Null, replayBody.RootElement.GetProperty("proofFileSizeBytes").ValueKind);

        await using var administrator2 = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator2.AuditEvents.CountAsync(audit =>
            audit.Action == "paguai.draft.create" && audit.ResourceId == draftId));
        // O recibo persiste apenas o ponteiro: nenhum metadado de arquivo é
        // duplicado fora de foundation.files.
        var receipt = await administrator2.IdempotencyRecords.SingleAsync(record =>
            record.CompanyId == scenario.CompanyId && record.RequestId == request.RequestId);
        Assert.DoesNotContain("replay.pdf", receipt.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("application/pdf", receipt.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_with_a_different_proof_returns_idempotency_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var first = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "a.pdf");
        var second = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "b.pdf");
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var payload = new Dictionary<string, object?>
        {
            ["companyId"] = scenario.CompanyId,
            ["requestId"] = requestId,
            ["title"] = "Office supplies",
            ["description"] = "Replacement printer paper",
            ["amount"] = "1250.00",
            ["currency"] = "BRL",
            ["dueDate"] = new DateOnly(2026, 9, 1),
            ["beneficiaryDisplayName"] = "Fornecedor Demo",
            ["proofRequirement"] = "required",
            ["proofFileId"] = first.Id,
        };

        using var original = await client.PostAsJsonAsync("/paguai/requests", payload);
        payload["proofFileId"] = second.Id;
        using var different = await client.PostAsJsonAsync("/paguai/requests", payload);

        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("{\"code\":\"idempotency_conflict\"}", await different.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Parallel_identical_creates_with_the_same_proof_reconcile_as_replay()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "unico.pdf");
        using var client = factory.CreateClient();
        var payload = ProofRequest(scenario.CompanyId, proof.Id);

        // Genuinamente paralelos: mesma chave idempotente, mesmo comprovante,
        // chegando juntos à persistência — o perdedor do 23505 reconcilia o
        // recibo commitado pelo vencedor e responde 200, nunca 409.
        var first = client.PostAsJsonAsync("/paguai/requests", payload);
        using var second = await client.PostAsJsonAsync("/paguai/requests", payload);
        using var firstResponse = await first;

        var codes = new[] { firstResponse.StatusCode, second.StatusCode };
        Assert.Contains(HttpStatusCode.Created, codes);
        Assert.Contains(HttpStatusCode.OK, codes);
        Assert.DoesNotContain(HttpStatusCode.Conflict, codes);
        Guid draftId;
        string winnerBody;
        if (firstResponse.StatusCode == HttpStatusCode.Created)
        {
            winnerBody = await firstResponse.Content.ReadAsStringAsync();
            var replayBody = await second.Content.ReadAsStringAsync();
            Assert.Equal(
                ReadGuid(JsonDocument.Parse(winnerBody).RootElement, "id"),
                ReadGuid(JsonDocument.Parse(replayBody).RootElement, "id"));
            draftId = ReadGuid(JsonDocument.Parse(winnerBody).RootElement, "id");
        }
        else
        {
            winnerBody = await second.Content.ReadAsStringAsync();
            var replayBody = await firstResponse.Content.ReadAsStringAsync();
            Assert.Equal(
                ReadGuid(JsonDocument.Parse(winnerBody).RootElement, "id"),
                ReadGuid(JsonDocument.Parse(replayBody).RootElement, "id"));
            draftId = ReadGuid(JsonDocument.Parse(winnerBody).RootElement, "id");
        }
        Assert.Equal(proof.Id, ReadGuid(JsonDocument.Parse(winnerBody).RootElement, "proofFileId"));

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.PaguaiRequests.CountAsync(request =>
            request.ProofFileId == proof.Id));
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "paguai.draft.create"
            && audit.ResourceId == draftId
            && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Parallel_divergent_creates_under_one_request_id_end_in_idempotency_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var first = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "a.pdf");
        var second = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "b.pdf");
        using var client = factory.CreateClient();
        // Mesma chave idempotente, comprovantes distintos: o perdedor do
        // recibo recebe 409 idempotency_conflict, não duplo vínculo.
        var requestId = Guid.NewGuid();
        var payloadA = ProofRequest(scenario.CompanyId, first.Id, requestId: requestId);
        var payloadB = ProofRequest(scenario.CompanyId, second.Id, requestId: requestId);

        var winner = client.PostAsJsonAsync("/paguai/requests", payloadA);
        using var loser = await client.PostAsJsonAsync("/paguai/requests", payloadB);
        using var winnerResponse = await winner;

        var codes = new[] { winnerResponse.StatusCode, loser.StatusCode };
        Assert.Contains(HttpStatusCode.Created, codes);
        Assert.Contains(HttpStatusCode.Conflict, codes);
        var conflict = winnerResponse.StatusCode == HttpStatusCode.Conflict ? winnerResponse : loser;
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var bound = await administrator.PaguaiRequests.SingleAsync(request =>
            request.ProofFileId == first.Id || request.ProofFileId == second.Id);
        Assert.Equal(1, await administrator.PaguaiRequests.CountAsync());
    }

    [Fact]
    public async Task Another_draft_binding_the_same_proof_gets_typed_proof_file_bound()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "disputado.pdf");
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, proof.Id));
        using var second = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, proof.Id));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("{\"code\":\"proof_file_bound\"}", await second.Content.ReadAsStringAsync());

        // A barreira física: o índice parcial único rejeita o duplo vínculo no storage.
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
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
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", scenario.CompanyId);
        command.Parameters.AddWithValue("requester", factory.ActorUserId);
        command.Parameters.AddWithValue("request_id", Guid.NewGuid());
        command.Parameters.AddWithValue("proof_file_id", proof.Id);
        var conflict = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
        var postgresConflict = Assert.IsType<PostgresException>(conflict);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresConflict.SqlState);
        Assert.Equal("uq_paguai_requests_proof_file", postgresConflict.ConstraintName);
    }

    [Fact]
    public async Task Legacy_receipt_cannot_replay_a_proof_bearing_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "legado.pdf");
        using var client = factory.CreateClient();
        var basePayload = ProofRequest(scenario.CompanyId, null);
        var draftId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.PaguaiRequests.Add(
                new PaguaiRequest
                {
                    Id = draftId,
                    CompanyId = scenario.CompanyId,
                    RequesterUserId = factory.ActorUserId,
                    RequestId = basePayload.RequestId,
                    Title = basePayload.Title,
                    Description = basePayload.Description,
                    Amount = 1250.00m,
                    Currency = "BRL",
                    DueDate = basePayload.DueDate,
                    BeneficiaryDisplayName = basePayload.BeneficiaryDisplayName,
                    ProofRequirement = basePayload.ProofRequirement,
                    State = "rascunho",
                    Version = 1,
                    CreatedAt = createdAt,
                });
            administrator.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    scenario.CompanyId,
                    factory.ActorUserId,
                    "paguai.draft.create",
                    basePayload.RequestId,
                    LegacyAmountMinorPayloadHash(basePayload),
                    JsonSerializer.Serialize(new
                    {
                        Id = draftId,
                        CompanyId = scenario.CompanyId,
                        RequestId = basePayload.RequestId,
                        Title = basePayload.Title,
                        Description = basePayload.Description,
                        AmountMinor = 125000L,
                        Currency = "BRL",
                        DueDate = basePayload.DueDate,
                        BeneficiaryDisplayName = basePayload.BeneficiaryDisplayName,
                        ProofRequirement = basePayload.ProofRequirement,
                        State = "rascunho",
                        Version = 1,
                        CreatedAt = createdAt,
                    })));
            await administrator.SaveChangesAsync();
        }

        // O mesmo payload agora com comprovante NÃO é replay do recibo legado:
        // 409 tipado, sem segundo vínculo nem auditoria de sucesso nova.
        var proofPayload = basePayload with { ProofFileId = proof.Id };
        using var withProof = await client.PostAsJsonAsync("/paguai/requests", proofPayload);
        Assert.Equal(HttpStatusCode.Conflict, withProof.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await withProof.Content.ReadAsStringAsync());

        // Controle: o mesmo payload SEM comprovante segue sendo replay legado 200.
        using var replay = await client.PostAsJsonAsync("/paguai/requests", basePayload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(draftId, ReadGuid(replayBody.RootElement, "id"));
        Assert.Equal(JsonValueKind.Null, replayBody.RootElement.GetProperty("proofFileId").ValueKind);

        await using var administrator2 = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator2.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Null(stored.ProofFileId);
        // O rascunho foi semeado direto (sem auditoria de criação) e replays
        // não auditam: nenhum sucesso novo pode ter surgido.
        Assert.Equal(0, await administrator2.AuditEvents.CountAsync(audit =>
            audit.ResourceId == draftId && audit.Outcome == AuditOutcome.Success));
        Assert.NotNull(await administrator2.AuditEvents.SingleOrDefaultAsync(audit =>
            audit.Action == "idempotency_conflict" && audit.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_proof_bound_create_and_its_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "vinculo.pdf");
        using var client = factory.CreateClient();
        // Uma primeira chamada admite a sessão enquanto a auditoria funciona;
        // caso contrário a revogação derruba o gate de identidade (503 de
        // identidade) antes de chegar ao serviço Paguai.
        using var warmup = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(scenario.CompanyId, null, proofRequirement: "optional"));
        Assert.Equal(HttpStatusCode.Created, warmup.StatusCode);

        await RevokeAuditAppendAsync(factory);
        var request = ProofRequest(scenario.CompanyId, proof.Id);
        using var unavailable = await client.PostAsJsonAsync("/paguai/requests", request);
        await GrantAuditAppendAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("{\"code\":\"paguai_unavailable\"}", await unavailable.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(await administrator.PaguaiRequests.AnyAsync(request =>
            request.ProofFileId == proof.Id));
        Assert.False(await administrator.AuditEvents.AnyAsync(audit =>
            audit.Outcome == AuditOutcome.Success && audit.Reason == "proof_attached"));
    }

    [Fact]
    public async Task Storage_signing_failure_returns_unavailable_without_altering_the_draft()
    {
        var storage = new StubProofStorage(throwOnSign: true);
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "assinatura.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        using var link = await client.GetAsync(
            $"/paguai/requests/{draftId}/proof-link?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, link.StatusCode);
        Assert.Equal("{\"code\":\"paguai_unavailable\"}", await link.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal(proof.Id, stored.ProofFileId);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Cas_race_resolves_single_winner_with_typed_loser_and_one_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var first = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "v1.pdf");
        var second = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "v2.pdf");
        var third = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "v3.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, first.Id);

        var winner = client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = third.Id, expectedVersion = 1 });
        var loser = client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = second.Id, expectedVersion = 1 });
        using var winnerResponse = await winner;
        using var loserResponse = await loser;

        var codes = new[] { winnerResponse.StatusCode, loserResponse.StatusCode };
        Assert.Contains(HttpStatusCode.OK, codes);
        Assert.Contains(HttpStatusCode.Conflict, codes);
        if (loserResponse.StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Equal(
                "{\"code\":\"proof_version_conflict\"}",
                await loserResponse.Content.ReadAsStringAsync());
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal(2, stored.Version);
        Assert.Equal("rascunho", stored.State);
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "paguai.draft.replace_proof"
            && audit.ResourceId == draftId
            && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_replacement_and_success_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "troca.pdf");
        var replacement = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "nova.pdf");
        using var client = factory.CreateClient();
        var draftId = await CreateDraftAsync(client, scenario.CompanyId, proof.Id);

        await RevokeAuditAppendAsync(factory);
        using var unavailable = await client.PatchAsJsonAsync(
            $"/paguai/requests/{draftId}/proof",
            new { companyId = scenario.CompanyId, proofFileId = replacement.Id, expectedVersion = 1 });
        await GrantAuditAppendAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("{\"code\":\"paguai_unavailable\"}", await unavailable.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.PaguaiRequests.SingleAsync(request => request.Id == draftId);
        Assert.Equal(1, stored.Version);
        Assert.Equal(proof.Id, stored.ProofFileId);
        Assert.False(await administrator.AuditEvents.AnyAsync(audit =>
            audit.Action == "paguai.draft.replace_proof" && audit.ResourceId == draftId));
    }

    [Fact]
    public async Task Browser_session_patch_requires_antiforgery_and_missing_draft_denials_are_audited()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = factory.CreateJwtToken(JwtTokenVariation.Valid) }),
        };
        var scenario = await SeedScenarioAsync(factory);
        var proof = await SeedFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "csrf.pdf");

        using var browser = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await SignInAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        using var missingCsrf = await browser.PatchAsJsonAsync(
            $"/paguai/requests/{Guid.NewGuid()}/proof",
            new { companyId = scenario.CompanyId, proofFileId = proof.Id, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var antiforgeryToken = await GetAntiforgeryTokenAsync(browser);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/paguai/requests/{Guid.NewGuid()}/proof")
        {
            Content = JsonContent.Create(
                new { companyId = scenario.CompanyId, proofFileId = proof.Id, expectedVersion = 1 }),
        };
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);
        using var withCsrf = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, withCsrf.StatusCode);
        Assert.Equal(
            "{\"code\":\"resource_not_found\"}",
            await withCsrf.Content.ReadAsStringAsync());

        // PATCH autenticado contra rascunho inexistente também deixa negação
        // durable com razão controlada, sem id de comprovante nem metadados.
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var denial = await administrator.AuditEvents.SingleAsync(audit =>
            audit.Action == "resource_not_found" && audit.Outcome == AuditOutcome.Denied);
        Assert.Equal("resource_not_found", denial.Reason);
        Assert.Equal("{}", denial.MetadataJson);

        using var openapi = await factory.CreateClient().GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await openapi.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("paths")
            .TryGetProperty("/paguai/requests/{id}/proof", out var patchPath));
        Assert.True(patchPath.TryGetProperty("patch", out _));
        Assert.True(document.RootElement.GetProperty("paths")
            .TryGetProperty("/paguai/requests/{id}/proof-link", out var linkPath));
        Assert.True(linkPath.TryGetProperty("get", out _));
    }

    private static async Task<Guid> CreateDraftAsync(
        HttpClient client, Guid companyId, Guid? proofFileId = null, string proofRequirement = "required")
    {
        using var created = await client.PostAsJsonAsync(
            "/paguai/requests", ProofRequest(companyId, proofFileId, proofRequirement));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ReadGuid(JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement, "id");
    }

    private static ProofRequestBody ProofRequest(
        Guid companyId,
        Guid? proofFileId,
        string proofRequirement = "required",
        Guid? requestId = null) =>
        new(
            companyId,
            requestId ?? Guid.NewGuid(),
            "Office supplies",
            "Replacement printer paper",
            "1250.00",
            "BRL",
            new DateOnly(2026, 9, 1),
            "Fornecedor Demo",
            proofRequirement,
            proofFileId);

    private static async Task<ProofFile> SeedFileAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid uploaderMembershipId,
        string fileName,
        string classification = "paguai_request_proof",
        bool confirmed = true,
        DateTimeOffset? retentionUntil = null)
    {
        var fileId = Guid.NewGuid();
        var lifecycleRequestId = Guid.NewGuid();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var file = new FileRecord(
            fileId,
            companyId,
            uploaderMembershipId,
            fileName,
            Path.GetExtension(fileName),
            10,
            "application/pdf",
            $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName))).ToLowerInvariant()}",
            classification,
            "web-paguai-draft",
            retentionUntil ?? Retention,
            factory.ActorUserId,
            lifecycleRequestId);
        if (confirmed)
        {
            file.MarkVerified(factory.ActorUserId, lifecycleRequestId);
            file.MarkConfirmed(factory.ActorUserId, lifecycleRequestId);
        }

        administrator.Files.Add(file);
        await administrator.SaveChangesAsync();
        return new ProofFile(file.Id, file.ProviderKey, file.Checksum);
    }

    private static async Task<Guid> SeedForeignDraftAsync(
        FoundationApiFactory factory,
        ProofScenario scenario,
        Guid proofFileId)
    {
        var draftId = Guid.NewGuid();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.PaguaiRequests.Add(
            new PaguaiRequest
            {
                Id = draftId,
                CompanyId = scenario.CompanyId,
                RequesterUserId = scenario.OtherUserId,
                RequestId = Guid.NewGuid(),
                Title = "Rascunho alheio",
                Description = "Pertence a outro requerente",
                Amount = 10.00m,
                Currency = "BRL",
                DueDate = new DateOnly(2026, 10, 1),
                BeneficiaryDisplayName = "Fornecedor Demo",
                ProofRequirement = "optional",
                ProofFileId = proofFileId,
                State = "rascunho",
                Version = 1,
            });
        await administrator.SaveChangesAsync();
        return draftId;
    }

    /// <summary>Cria empresa estrangeira, memberships e grants paguai para o ator autenticado.</summary>
    private static async Task<ProofScenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        var foreignCompany = new Company(Guid.NewGuid(), group.Id, "Vinx Serviços", factory.ActorUserId, requestId);
        var membership = new Membership(
            Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var foreignMembership = new Membership(
            Guid.NewGuid(), foreignCompany.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var otherUserId = Guid.NewGuid();
        var otherUser = new User(otherUserId, "Outro Autor", $"other-{otherUserId:N}@vinx.test");
        var otherMembership = new Membership(
            Guid.NewGuid(), company.Id, otherUserId, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Paguai requester", factory.ActorUserId, requestId);
        var capabilities = await administrator.Capabilities
            .Where(capability => capability.Name == "paguai.draft.read" || capability.Name == "paguai.draft.create")
            .ToDictionaryAsync(capability => capability.Name);
        Assert.True(capabilities.ContainsKey("paguai.draft.read"));
        Assert.True(capabilities.ContainsKey("paguai.draft.create"));
        administrator.AddRange(
            group,
            company,
            foreignCompany,
            otherUser,
            membership,
            foreignMembership,
            otherMembership,
            role,
            new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, requestId),
            new RoleCapability(
                Guid.NewGuid(), company.Id, role.Id, capabilities["paguai.draft.read"].Id,
                CapabilityScope.Company, factory.ActorUserId, requestId),
            new RoleCapability(
                Guid.NewGuid(), company.Id, role.Id, capabilities["paguai.draft.create"].Id,
                CapabilityScope.Company, factory.ActorUserId, requestId));
        await administrator.SaveChangesAsync();
        return new ProofScenario(
            company.Id, foreignCompany.Id, membership.Id, foreignMembership.Id, otherMembership.Id, otherUserId);
    }

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "GRANT EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) TO vinx_runtime;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        Assert.True(root.TryGetProperty(propertyName, out var property));
        Assert.True(Guid.TryParse(property.GetString(), out var value));
        return value;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/foundation/identity/browser-session/antiforgery");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("requestToken").GetString()
            ?? throw new Xunit.Sdk.XunitException("The antiforgery token was empty.");
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/foundation/identity/browser-session")
        {
            Content = JsonContent.Create(new
            {
                email = "same-address@example.test",
                password = "safe-test-password",
            }),
        };
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);
        return await client.SendAsync(request);
    }

    private sealed class BrowserSessionProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(ResponseFactory?.Invoke(request)
                ?? throw new InvalidOperationException("The browser session provider response was not configured."));
    }

    /// <summary>Hash canônico legado (amountMinor) aceito na transição de recibos pré-corte.</summary>
    private static string LegacyAmountMinorPayloadHash(ProofRequestBody request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            companyId = request.CompanyId,
            requestId = request.RequestId,
            title = request.Title,
            description = request.Description,
            amountMinor = 125000L,
            currency = request.Currency,
            dueDate = request.DueDate,
            beneficiaryDisplayName = request.BeneficiaryDisplayName,
            proofRequirement = request.ProofRequirement,
        });
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
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

    private sealed record ProofFile(Guid Id, string ProviderKey, string Checksum);

    private sealed record ProofScenario(
        Guid CompanyId,
        Guid ForeignCompanyId,
        Guid MembershipId,
        Guid ForeignMembershipId,
        Guid OtherMembershipId,
        Guid OtherUserId);

    /// <summary>Dobra de FileStorage que registra (ou falha) cada assinatura de URL curta.</summary>
    private sealed class StubProofStorage(bool throwOnSign = false) : FileStorage
    {
        private readonly List<(string ProviderKey, TimeSpan Lifetime)> _attempts = [];

        public IReadOnlyList<(string ProviderKey, TimeSpan Lifetime)> Attempts => _attempts;

        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
            string providerKey, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            if (throwOnSign)
            {
                throw new InvalidOperationException("Signing is unavailable in this test.");
            }

            _attempts.Add((providerKey, lifetime));
            return Task.FromResult(new SignedStorageUrl(
                new Uri($"https://signed.storage.test/{providerKey}"),
                DateTimeOffset.UtcNow.Add(lifetime)));
        }

        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
            string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadObjectAsync(
            string providerKey, ReadOnlyMemory<byte> content, string contentType,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageObjectMetadata> GetObjectMetadataAsync(
            string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageObjectInspection> InspectObjectAsync(
            string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
            string prefix, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
