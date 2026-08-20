using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftEndpointTests
{
    [Fact]
    public async Task Draft_creation_returns_a_company_scoped_rascunho()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var response = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.NotEqual(Guid.Empty, ReadGuid(root, "id"));
        Assert.Equal(scenario.CompanyId, ReadGuid(root, "companyId"));
        Assert.Equal(request.RequestId, ReadGuid(root, "requestId"));
        Assert.Equal("Office supplies", root.GetProperty("title").GetString());
        Assert.Equal("Replacement printer paper", root.GetProperty("description").GetString());
        Assert.Equal(request.Amount, root.GetProperty("amount").GetString());
        Assert.Equal("BRL", root.GetProperty("currency").GetString());
        Assert.Equal("2026-09-01", root.GetProperty("dueDate").GetString());
        Assert.Equal("Fornecedor Demo", root.GetProperty("beneficiaryDisplayName").GetString());
        Assert.Equal("required", root.GetProperty("proofRequirement").GetString());
        Assert.Equal("rascunho", root.GetProperty("state").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Draft_creation_accepts_a_two_decimal_amount()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/paguai/requests",
            new
            {
                companyId = scenario.CompanyId,
                requestId = Guid.NewGuid(),
                title = "Office supplies",
                description = "Replacement printer paper",
                amount = "1250.25",
                currency = "BRL",
                dueDate = new DateOnly(2026, 9, 1),
                beneficiaryDisplayName = "Fornecedor Demo",
                proofRequirement = "required",
            }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1250.25", document.RootElement.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Equivalent_decimal_spellings_replay_the_same_draft()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var firstRequest = NewRequest(scenario.CompanyId, requestId) with { Amount = "1" };
        var equivalentRequest = firstRequest with { Amount = "1.00" };

        using var first = await client.PostAsJsonAsync("/paguai/requests", firstRequest);
        using var replay = await client.PostAsJsonAsync("/paguai/requests", equivalentRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            ReadGuid(firstDocument.RootElement, "id"),
            ReadGuid(replayDocument.RootElement, "id")
        );
        Assert.Equal("1.00", replayDocument.RootElement.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Legacy_integer_receipt_replays_after_decimal_contract_upgrade()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());
        var draftId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var legacyResultJson = JsonSerializer.Serialize(
            new
            {
                Id = draftId,
                CompanyId = request.CompanyId,
                RequestId = request.RequestId,
                Title = request.Title,
                Description = request.Description,
                AmountMinor = 125000L,
                Currency = request.Currency,
                DueDate = request.DueDate,
                BeneficiaryDisplayName = request.BeneficiaryDisplayName,
                ProofRequirement = request.ProofRequirement,
                State = "rascunho",
                Version = 1,
                CreatedAt = createdAt,
            }
        );

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.PaguaiRequests.Add(
                new PaguaiRequest
                {
                    Id = draftId,
                    CompanyId = request.CompanyId,
                    RequesterUserId = factory.ActorUserId,
                    RequestId = request.RequestId,
                    Title = request.Title,
                    Description = request.Description,
                    Amount = 1250.00m,
                    Currency = request.Currency,
                    DueDate = request.DueDate,
                    BeneficiaryDisplayName = request.BeneficiaryDisplayName,
                    ProofRequirement = request.ProofRequirement,
                    State = "rascunho",
                    Version = 1,
                    CreatedAt = createdAt,
                }
            );
            administrator.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    request.CompanyId,
                    factory.ActorUserId,
                    "paguai.draft.create",
                    request.RequestId,
                    LegacyPayloadHash(request, 125000),
                    legacyResultJson
                )
            );
            await administrator.SaveChangesAsync();
        }

        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/paguai/requests",
            request
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(draftId, ReadGuid(document.RootElement, "id"));
        Assert.Equal("1250.00", document.RootElement.GetProperty("amount").GetString());
        await using var audit = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            0,
            await audit.AuditEvents.CountAsync(auditEvent =>
                auditEvent.Action == "paguai.draft.create"
                && auditEvent.RequestedCompanyId == request.CompanyId
            )
        );
    }

    [Fact]
    public async Task Replaying_same_payload_returns_same_draft_and_different_payload_conflicts()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var first = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstDraftId = ReadGuid(firstDocument.RootElement, "id");

        using var replay = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstDraftId, ReadGuid(replayDocument.RootElement, "id"));
        Assert.Equal(
            "Office supplies",
            replayDocument.RootElement.GetProperty("title").GetString()
        );

        using var conflict = await client.PostAsJsonAsync(
            "/paguai/requests",
            request with
            {
                Title = "Different title",
            }
        );

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync()
        );

        using var list = await client.GetAsync($"/paguai/requests?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var drafts = listDocument.RootElement.EnumerateArray().ToList();
        Assert.Equal(firstDraftId, ReadGuid(Assert.Single(drafts), "id"));
    }

    [Fact]
    public async Task Draft_reads_are_visible_to_member_company_only()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var create = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createDocument.RootElement, "id");

        using var detail = await client.GetAsync(
            $"/paguai/requests/{draftId}?companyId={scenario.CompanyId}"
        );

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailDocument = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(draftId, ReadGuid(detailDocument.RootElement, "id"));
        Assert.Equal(
            "Office supplies",
            detailDocument.RootElement.GetProperty("title").GetString()
        );

        using var foreignDetail = await client.GetAsync(
            $"/paguai/requests/{draftId}?companyId={scenario.ForeignCompanyId}"
        );

        Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);
        Assert.Equal(
            "{\"code\":\"resource_not_found\"}",
            await foreignDetail.Content.ReadAsStringAsync()
        );

        using var foreignList = await client.GetAsync(
            $"/paguai/requests?companyId={scenario.ForeignCompanyId}"
        );
        var foreignListBody = await foreignList.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, foreignList.StatusCode);
        Assert.DoesNotContain("Office supplies", foreignListBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_creation_requires_the_company_create_capability()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator
                .RoleCapabilities.Where(grant =>
                    grant.CompanyId == scenario.CompanyId
                    && grant.CapabilityId == KnownCapabilities.PaguaiDraftCreateId
                )
                .ExecuteDeleteAsync();
        }

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/paguai/requests",
            NewRequest(scenario.CompanyId, Guid.NewGuid())
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"capability_denied\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Draft_creation_rejects_a_non_json_content_type_before_any_mutation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());
        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "text/plain"
        );
        using var response = await client.PostAsync("/paguai/requests", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
        await AssertNoPersistedDraftOrReceiptAsync(factory, scenario.CompanyId, request.RequestId);
    }

    [Fact]
    public async Task Draft_creation_rejects_a_body_larger_than_the_explicit_http_limit_before_any_mutation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid()) with
        {
            Description = new string('a', 25 * 1024),
        };

        using var response = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
        await AssertNoPersistedDraftOrReceiptAsync(factory, scenario.CompanyId, request.RequestId);
    }

    [Fact]
    public async Task Draft_create_openapi_declares_json_body_antiforgery_and_safe_error_responses()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/paguai/requests")
            .GetProperty("post");
        var requestBody = operation.GetProperty("requestBody");

        Assert.True(requestBody.GetProperty("required").GetBoolean());
        Assert.True(
            requestBody
                .GetProperty("content")
                .TryGetProperty("application/json", out var applicationJson)
        );
        var schema = applicationJson.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(
            [
                "amount",
                "beneficiaryDisplayName",
                "companyId",
                "currency",
                "description",
                "dueDate",
                "proofRequirement",
                "requestId",
                "title",
            ],
            schema
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Order()
        );
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties");
        Assert.Equal("uuid", properties.GetProperty("companyId").GetProperty("format").GetString());
        Assert.Equal("uuid", properties.GetProperty("requestId").GetProperty("format").GetString());
        Assert.Equal(200, properties.GetProperty("title").GetProperty("maxLength").GetInt32());
        Assert.Equal(
            4000,
            properties.GetProperty("description").GetProperty("maxLength").GetInt32()
        );
        var amount = properties.GetProperty("amount");
        Assert.Equal("string", amount.GetProperty("type").GetString());
        Assert.Equal(
            "^\\d+(?:\\.\\d{1,2})?$",
            amount.GetProperty("pattern").GetString()
        );
        Assert.Equal(0.01m, amount.GetProperty("minimum").GetDecimal());
        Assert.Equal(
            9_999_999_999_999_999.99m,
            amount.GetProperty("maximum").GetDecimal()
        );
        Assert.Equal("BRL", properties.GetProperty("currency").GetProperty("enum")[0].GetString());
        Assert.Equal("date", properties.GetProperty("dueDate").GetProperty("format").GetString());
        Assert.Equal(200, properties.GetProperty("beneficiaryDisplayName").GetProperty("maxLength").GetInt32());
        Assert.Equal(
            ["optional", "required"],
            properties.GetProperty("proofRequirement").GetProperty("enum").EnumerateArray().Select(value => value.GetString())
        );
        Assert.Contains(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter =>
                parameter.GetProperty("name").GetString() == "X-CSRF-TOKEN"
                && parameter.GetProperty("in").GetString() == "header"
                && (
                    !parameter.TryGetProperty("required", out var required)
                    || !required.GetBoolean()
                )
        );
        var responses = operation.GetProperty("responses");
        foreach (
            var status in new[] { "200", "201", "400", "401", "403", "409", "415", "429", "503" }
        )
        {
            Assert.True(responses.TryGetProperty(status, out _));
        }
    }

    [Fact]
    public async Task Draft_creation_appends_exactly_one_safe_success_audit_event_and_replay_does_not_append_another()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var created = await client.PostAsJsonAsync("/paguai/requests", request);
        using var replay = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createdDocument.RootElement, "id");
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var events = await administrator
            .AuditEvents.Where(auditEvent =>
                auditEvent.Action == "paguai.draft.create"
                && auditEvent.ResourceType == "paguai_request"
                && auditEvent.ResourceId == draftId
            )
            .ToListAsync();
        var auditEvent = Assert.Single(events);

        Assert.Equal("{}", auditEvent.MetadataJson);
        Assert.Null(auditEvent.Reason);
        Assert.Equal(factory.ActorUserId, auditEvent.ActorUserId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    public async Task Draft_creation_rejects_invalid_decimal_amount(string amount)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/paguai/requests",
            NewRequest(scenario.CompanyId, Guid.NewGuid()) with { Amount = amount }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Draft_creation_rejects_a_numeric_amount()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var numericAmount = JsonSerializer.Serialize(
            new
            {
                companyId = scenario.CompanyId,
                requestId = Guid.NewGuid(),
                title = "Office supplies",
                description = "Replacement printer paper",
                amount = 1250.25m,
                currency = "BRL",
                dueDate = new DateOnly(2026, 9, 1),
                beneficiaryDisplayName = "Fornecedor Demo",
                proofRequirement = "required",
            }
        );
        using var response = await factory.CreateClient().PostAsync(
            "/paguai/requests",
            new StringContent(numericAmount, Encoding.UTF8, "application/json")
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Draft_creation_rejects_invalid_currency_due_date_and_proof_requirement()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        var invalidCurrency = NewRequest(scenario.CompanyId, Guid.NewGuid()) with { Currency = "USD" };
        var invalidProof = NewRequest(scenario.CompanyId, Guid.NewGuid()) with { ProofRequirement = "unknown" };
        var invalidDueDate = new
        {
            companyId = scenario.CompanyId,
            requestId = Guid.NewGuid(),
            title = "Office supplies",
            description = "Replacement printer paper",
            amount = "1250.00",
            currency = "BRL",
            dueDate = "2026-02-30",
            beneficiaryDisplayName = "Fornecedor Demo",
            proofRequirement = "required",
        };

        using var currencyResponse = await client.PostAsJsonAsync("/paguai/requests", invalidCurrency);
        using var proofResponse = await client.PostAsJsonAsync("/paguai/requests", invalidProof);
        using var dueDateResponse = await client.PostAsJsonAsync("/paguai/requests", invalidDueDate);

        Assert.Equal(HttpStatusCode.BadRequest, currencyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, proofResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dueDateResponse.StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_draft_and_idempotency_then_the_same_request_can_retry()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());
        using var admission = await client.GetAsync("/foundation/bootstrap");
        Assert.Equal(HttpStatusCode.OK, admission.StatusCode);
        await RevokeAuditAppendAsync(factory);

        using var failed = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("{\"code\":\"paguai_unavailable\"}", await failed.Content.ReadAsStringAsync());
        await AssertNoPersistedDraftOrReceiptAsync(factory, scenario.CompanyId, request.RequestId);
        await GrantAuditAppendAsync(factory);

        using var retry = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        await AssertPersistedDraftAndReceiptAsync(factory, scenario.CompanyId, request.RequestId);
    }

    [Fact]
    public async Task Concurrent_identical_draft_creates_return_one_creation_one_replay_and_one_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/paguai/requests", request),
            secondClient.PostAsJsonAsync("/paguai/requests", request)
        );
        try
        {
            Assert.Equal(
                [HttpStatusCode.OK, HttpStatusCode.Created],
                responses.Select(response => response.StatusCode).Order()
            );
            await AssertPersistedDraftAndReceiptAsync(
                factory,
                scenario.CompanyId,
                request.RequestId
            );
            await using var administrator = factory.Fixture.CreateAdministratorDbContext();
            Assert.Equal(
                1,
                await administrator.AuditEvents.CountAsync(auditEvent =>
                    auditEvent.Action == "paguai.draft.create"
                    && auditEvent.RequestedCompanyId == scenario.CompanyId
                )
            );
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    private static DraftRequest NewRequest(Guid companyId, Guid requestId) =>
        new(
            companyId,
            requestId,
            "Office supplies",
            "Replacement printer paper",
            "1250.00",
            "BRL",
            new DateOnly(2026, 9, 1),
            "Fornecedor Demo",
            "required"
        );

    private static string LegacyPayloadHash(DraftRequest request, long amountMinor)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                companyId = request.CompanyId,
                requestId = request.RequestId,
                title = request.Title,
                description = request.Description,
                amountMinor,
                currency = request.Currency,
                dueDate = request.DueDate,
                beneficiaryDisplayName = request.BeneficiaryDisplayName,
                proofRequirement = request.ProofRequirement,
            }
        );
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        Assert.True(root.TryGetProperty(propertyName, out var property));
        Assert.True(Guid.TryParse(property.GetString(), out var value));
        return value;
    }

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "GRANT EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) TO vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertNoPersistedDraftOrReceiptAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid requestId
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == companyId
                && record.ActorUserId == factory.ActorUserId
                && record.CommandName == "paguai.draft.create"
                && record.RequestId == requestId
            )
        );
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM paguai.requests WHERE company_id = @company_id AND request_id = @request_id",
            connection
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("request_id", requestId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static async Task AssertPersistedDraftAndReceiptAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid requestId
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.True(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == companyId
                && record.ActorUserId == factory.ActorUserId
                && record.CommandName == "paguai.draft.create"
                && record.RequestId == requestId
            )
        );
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM paguai.requests WHERE company_id = @company_id AND request_id = @request_id",
            connection
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("request_id", requestId);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private static async Task<Scenario> SeedScenarioAsync(FoundationApiFactory factory)
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
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Paguai requester",
            factory.ActorUserId,
            requestId
        );
        var readCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == "paguai.draft.read"
        );
        if (readCapability is null)
        {
            readCapability = new Capability(
                Guid.Parse("766bdb56-80c0-5dee-982f-be7f0d3eaea8"),
                "paguai.draft.read"
            );
            administrator.Capabilities.Add(readCapability);
        }

        var createCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == "paguai.draft.create"
        );
        if (createCapability is null)
        {
            createCapability = new Capability(
                Guid.Parse("aecaa296-6a6f-5815-93ac-bd1ccdff3f44"),
                "paguai.draft.create"
            );
            administrator.Capabilities.Add(createCapability);
        }

        var readGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            readCapability.Id,
            CapabilityScope.Company,
            factory.ActorUserId,
            requestId
        );
        var createGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            createCapability.Id,
            CapabilityScope.Company,
            factory.ActorUserId,
            requestId
        );
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            membership.Id,
            factory.ActorUserId,
            requestId
        );

        administrator.AddRange(
            group,
            company,
            foreignCompany,
            membership,
            role,
            readGrant,
            createGrant,
            assignment
        );
        await administrator.SaveChangesAsync();

        return new Scenario(company.Id, foreignCompany.Id);
    }

    private sealed record DraftRequest(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description,
        string Amount,
        string Currency,
        DateOnly DueDate,
        string BeneficiaryDisplayName,
        string ProofRequirement
    );

    private sealed record Scenario(Guid CompanyId, Guid ForeignCompanyId);
}
