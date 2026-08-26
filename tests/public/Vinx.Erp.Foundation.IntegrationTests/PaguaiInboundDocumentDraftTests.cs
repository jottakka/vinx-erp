using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Paguai;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// BDD do rascunho Pagaí a partir de documento inbound (#272 PR4): candidato
/// válido vira um único rascunho idempotente, vinculado ao documento e
/// auditado; campos não garantidos pela fatura são sempre explícitos.
/// </summary>
public sealed class PaguaiInboundDocumentDraftTests
{
    [Fact]
    public void Existing_positional_source_argument_remains_the_inbound_document()
    {
        var sourceInboundMessageId = Guid.NewGuid();
        var command = new CreateDraftCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Aluguel setembro",
            "Rascunho do recibo enviado por WhatsApp",
            250.00m,
            "BRL",
            new DateOnly(2026, 9, 5),
            "ACME LTDA",
            "required",
            sourceInboundMessageId
        );

        Assert.Null(command.InvoiceFileId);
        Assert.Equal(sourceInboundMessageId, command.SourceInboundMessageId);
    }

    [Fact]
    public async Task Valid_candidate_creates_one_idempotent_draft_linked_to_the_document()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, candidateState: "candidate");
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/paguai/requests/from-document",
            RequestBody(scenario)
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var root = firstDocument.RootElement;
        var draftId = ReadGuid(root, "id");
        Assert.NotEqual(Guid.Empty, draftId);
        Assert.Equal(scenario.CompanyId, ReadGuid(root, "companyId"));
        // RequestId determinístico = id da admissão inbound.
        Assert.Equal(scenario.InboundMessageId, ReadGuid(root, "requestId"));
        Assert.Equal("250.00", root.GetProperty("amount").GetString());
        Assert.Equal("ACME LTDA", root.GetProperty("beneficiaryDisplayName").GetString());
        Assert.Equal("2026-09-05", root.GetProperty("dueDate").GetString());
        Assert.Equal("Aluguel setembro", root.GetProperty("title").GetString());
        Assert.Equal("required", root.GetProperty("proofRequirement").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("invoiceFileId").ValueKind);

        using var replay = await client.PostAsJsonAsync(
            "/paguai/requests/from-document",
            RequestBody(scenario)
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(draftId, ReadGuid(replayDocument.RootElement, "id"));

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT source_inbound_message_id, invoice_file_id FROM paguai.requests WHERE id = @id",
            administrator
        );
        command.Parameters.AddWithValue("id", draftId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(scenario.InboundMessageId, reader.GetGuid(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Needs_review_candidate_is_rejected()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory, candidateState: "needs_review");
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/paguai/requests/from-document",
            RequestBody(scenario)
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Candidate_of_another_company_is_not_visible()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            candidateState: "candidate",
            candidateCompany: Guid.NewGuid()
        );
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/paguai/requests/from-document",
            RequestBody(scenario)
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_capability_is_denied()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            candidateState: "candidate",
            withCapability: false
        );
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/paguai/requests/from-document",
            RequestBody(scenario)
        );
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Read_function_privileges_are_closed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            (bool)(
                await ScalarAsync(
                    administrator,
                    "has_function_privilege('vinx_runtime', 'notifications.read_inbound_whatsapp_invoice_candidate(uuid)', 'EXECUTE')"
                )
            )!
        );
        Assert.False(
            (bool)(
                await ScalarAsync(
                    administrator,
                    "has_function_privilege('vinx_worker', 'notifications.read_inbound_whatsapp_invoice_candidate(uuid)', 'EXECUTE')"
                )
            )!
        );
        Assert.NotNull(
            await ScalarAsync(
                administrator,
                "to_regclass('paguai.uq_paguai_requests_source_inbound_message')::text"
            )
        );

        static async Task<object?> ScalarAsync(NpgsqlConnection connection, string expression)
        {
            await using var command = new NpgsqlCommand($"SELECT {expression}", connection);
            return await command.ExecuteScalarAsync();
        }
    }

    private static object RequestBody(Scenario scenario) =>
        new
        {
            companyId = scenario.CompanyId,
            inboundMessageId = scenario.InboundMessageId,
            title = "Aluguel setembro",
            description = "Rascunho do recibo enviado por WhatsApp",
            proofRequirement = "required",
        };

    private sealed record Scenario(Guid CompanyId, Guid InboundMessageId);

    private static async Task<Scenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        string candidateState,
        bool withCapability = true,
        Guid? candidateCompany = null
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Obras",
            factory.ActorUserId,
            requestId
        );
        companyId = company.Id;
        var membership = new Membership(
            Guid.NewGuid(),
            companyId,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        administrator.AddRange(group, company, membership);
        if (withCapability)
        {
            var createCapability = await administrator.Capabilities.SingleOrDefaultAsync(
                capability => capability.Name == KnownCapabilities.PaguaiDraftCreate
            );
            if (createCapability is null)
            {
                createCapability = new Capability(
                    KnownCapabilities.PaguaiDraftCreateId,
                    KnownCapabilities.PaguaiDraftCreate
                );
                administrator.Capabilities.Add(createCapability);
            }

            var role = new Role(
                Guid.NewGuid(),
                companyId,
                "Paguai requester",
                factory.ActorUserId,
                requestId
            );
            var grant = new RoleCapability(
                Guid.NewGuid(),
                companyId,
                role.Id,
                createCapability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            );
            var assignment = new RoleAssignment(
                Guid.NewGuid(),
                companyId,
                role.Id,
                membership.Id,
                factory.ActorUserId,
                requestId
            );
            administrator.AddRange(role, grant, assignment);
        }

        await administrator.SaveChangesAsync();

        var inboundId = Guid.NewGuid();
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
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
            VALUES (@id, @candidate_company, @user_id, @state,
                CASE WHEN @state = 'candidate' THEN 'ACME LTDA' ELSE NULL END,
                CASE WHEN @state = 'candidate' THEN '250.00' ELSE NULL END,
                CASE WHEN @state = 'candidate' THEN 'BRL' ELSE NULL END,
                CASE WHEN @state = 'candidate' THEN DATE '2026-09-05' ELSE NULL END,
                CASE WHEN @state = 'candidate' THEN 'NF 123' ELSE NULL END,
                CASE WHEN @state = 'needs_review' THEN 'missing_amount' ELSE NULL END)
            """,
            connection
        );
        seed.Parameters.AddWithValue("id", inboundId);
        seed.Parameters.AddWithValue("message_id", $"wamid.doc.{Guid.NewGuid():N}");
        seed.Parameters.AddWithValue("media_id", $"media.{Guid.NewGuid():N}");
        seed.Parameters.AddWithValue("user_id", factory.ActorUserId);
        seed.Parameters.AddWithValue("company_id", companyId);
        seed.Parameters.AddWithValue("membership_id", membership.Id);
        seed.Parameters.AddWithValue("candidate_company", candidateCompany ?? companyId);
        seed.Parameters.AddWithValue("state", candidateState);
        await seed.ExecuteNonQueryAsync();

        return new Scenario(companyId, inboundId);
    }

    private static Guid ReadGuid(JsonElement root, string name) =>
        root.GetProperty(name).GetString() is { } value && Guid.TryParse(value, out var parsed)
            ? parsed
            : root.GetProperty(name).GetGuid();
}
