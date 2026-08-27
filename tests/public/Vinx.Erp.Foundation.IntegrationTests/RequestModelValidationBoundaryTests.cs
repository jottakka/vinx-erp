using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Fronteira de validação dos models de request inferidos no OpenAPI: toda
/// variação inválida (campo ausente, null, Guid vazio, expectedVersion 0,
/// string vazia) recebe 400 invalid_request sem efeito persistido.
/// </summary>
public sealed class RequestModelValidationBoundaryTests
{
    [Fact]
    public async Task Paguai_draft_create_rejects_invalid_request_models_without_persisting_anything()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var cases = new List<object>
        {
            // companyId ausente.
            new { requestId, title = "T", description = "D" },
            new
            {
                companyId = (Guid?)null,
                requestId,
                title = "T",
                description = "D",
            },
            new
            {
                companyId = (Guid?)Guid.Empty,
                requestId,
                title = "T",
                description = "D",
            },
            new
            {
                companyId = (Guid?)scenario.CompanyId,
                requestId = Guid.Empty,
                title = "T",
                description = "D",
            },
            new
            {
                companyId = (Guid?)scenario.CompanyId,
                requestId,
                title = (string?)null,
                description = "D",
            },
            new
            {
                companyId = (Guid?)scenario.CompanyId,
                requestId,
                title = string.Empty,
                description = "D",
            },
            // description ausente.
            new { companyId = scenario.CompanyId, requestId, title = "Sem descrição" },
        };

        foreach (var payload in cases)
        {
            using var response = await client.PostAsJsonAsync("/paguai/requests", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
        Assert.Equal(
            0L,
            await administrator
                .Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM paguai.requests WHERE company_id = {scenario.CompanyId}"
                )
                .SingleAsync()
        );
    }

    [Fact]
    public async Task Atendimento_card_create_rejects_invalid_request_models_without_persisting_anything()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var cases = new List<object>
        {
            // companyId ausente.
            new { requestId, title = "T", description = "D" },
            new
            {
                companyId = (Guid?)Guid.Empty,
                requestId,
                title = "T",
                description = "D",
            },
            new
            {
                companyId = (Guid?)scenario.CompanyId,
                requestId = Guid.Empty,
                title = "T",
                description = "D",
            },
            new
            {
                companyId = (Guid?)scenario.CompanyId,
                requestId,
                title = (string?)null,
                description = "D",
            },
            new
            {
                companyId = (Guid?)scenario.CompanyId,
                requestId,
                title = string.Empty,
                description = "D",
            },
            // description ausente.
            new { companyId = scenario.CompanyId, requestId, title = "Sem descrição" },
        };

        foreach (var payload in cases)
        {
            using var response = await client.PostAsJsonAsync("/atendimento/cards", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
        Assert.Equal(
            0L,
            await administrator
                .Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_cards WHERE company_id = {scenario.CompanyId}"
                )
                .SingleAsync()
        );
    }

    [Fact]
    public async Task Atendimento_card_create_rejects_empty_attachment_or_participant_ids_without_persisting_anything()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var cases = new object[]
        {
            new
            {
                companyId = scenario.CompanyId,
                requestId = requestIds[0],
                title = "T",
                description = "D",
                attachmentFileId = Guid.Empty,
            },
            new
            {
                companyId = scenario.CompanyId,
                requestId = requestIds[1],
                title = "T",
                description = "D",
                participantMembershipIds = new[] { Guid.Empty },
            },
        };

        foreach (var payload in cases)
        {
            using var response = await client.PostAsJsonAsync("/atendimento/cards", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && requestIds.Contains(record.RequestId)
            )
        );
        Assert.Equal(
            0L,
            await administrator.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_cards WHERE company_id = {scenario.CompanyId}"
            ).SingleAsync()
        );
    }

    [Fact]
    public async Task Atendimento_assign_rejects_invalid_request_models_without_changing_the_card()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var requestIds = new List<Guid>();
        var cases = new List<Func<object>>
        {
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                assigneeMembershipId = Guid.Empty,
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = 0L,
                assigneeMembershipId = scenario.AssigneeMembershipId,
            },
            // expectedVersion ausente.
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                assigneeMembershipId = scenario.AssigneeMembershipId,
            },
            () => new
            {
                companyId = Guid.Empty,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                assigneeMembershipId = scenario.AssigneeMembershipId,
            },
        };

        foreach (var payload in cases)
        {
            using var response = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/assign",
                payload()
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }

        await AssertCardUnchangedAndNoReceiptAsync(factory, client, scenario, created, requestIds);
    }

    [Fact]
    public async Task Atendimento_move_rejects_invalid_request_models_without_changing_the_card()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var requestIds = new List<Guid>();
        var cases = new List<Func<object>>
        {
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                column = string.Empty,
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                column = (string?)null,
            },
            // column ausente.
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = 0L,
                column = "em_andamento",
            },
        };

        foreach (var payload in cases)
        {
            using var response = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/move",
                payload()
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }

        await AssertCardUnchangedAndNoReceiptAsync(factory, client, scenario, created, requestIds);
    }

    [Fact]
    public async Task Atendimento_cancel_rejects_invalid_or_untrimmed_models_without_changing_the_card()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var requestIds = new List<Guid>();
        var cases = new List<Func<object>>
        {
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = Guid.Empty,
                expectedVersion = created.Version,
                reason = "Cancelamento válido.",
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                reason = string.Empty,
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = 0L,
                reason = "Cancelamento válido.",
            },
            () => new
            {
                companyId = Guid.Empty,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                reason = "Cancelamento válido.",
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                reason = " Não aparado ",
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                reason = (string?)null,
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                reason = "Cancelamento válido.",
            },
            () => new
            {
                companyId = scenario.CompanyId,
                requestId = NextRequestId(requestIds),
                expectedVersion = created.Version,
                reason = new string('a', 501),
            },
        };

        foreach (var payload in cases)
        {
            using var response = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/cancel",
                payload()
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }

        await AssertCardUnchangedAndNoReceiptAsync(factory, client, scenario, created, requestIds);

        var routedRequestId = Guid.NewGuid();
        using var emptyRoute = await client.PostAsJsonAsync(
            $"/atendimento/cards/{Guid.Empty}/cancel",
            new
            {
                companyId = scenario.CompanyId,
                requestId = routedRequestId,
                expectedVersion = created.Version,
                reason = "Cancelamento válido.",
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, emptyRoute.StatusCode);
        await AssertCardUnchangedAndNoReceiptAsync(
            factory,
            client,
            scenario,
            created,
            [routedRequestId]
        );
    }

    private static Guid NextRequestId(List<Guid> requestIds)
    {
        var requestId = Guid.NewGuid();
        requestIds.Add(requestId);
        return requestId;
    }

    private static async Task<CardResponse> CreateCardAsync(HttpClient client, Guid companyId)
    {
        using var response = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                companyId,
                Guid.NewGuid(),
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardResponse>())!;
    }

    private static async Task AssertCardUnchangedAndNoReceiptAsync(
        FoundationApiFactory factory,
        HttpClient client,
        Scenario scenario,
        CardResponse created,
        IReadOnlyList<Guid> requestIds
    )
    {
        using var queue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var cards = await queue.Content.ReadFromJsonAsync<List<CardResponse>>();
        var current = Assert.Single(cards!, card => card.Id == created.Id);
        Assert.Equal(created.Column, current.Column);
        Assert.Equal(created.Version, current.Version);
        Assert.Equal(created.AssigneeMembershipId, current.AssigneeMembershipId);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && requestIds.Contains(record.RequestId)
            )
        );
    }

    private static async Task<Scenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Fronteira",
            factory.ActorUserId,
            requestId
        );
        var actorMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var assignee = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var assigneeMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            assignee.Id,
            factory.ActorUserId,
            requestId
        );
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Fronteira administrator",
            factory.ActorUserId,
            requestId
        );
        var capabilityNames = new[]
        {
            KnownCapabilities.ServiceDeskTicketReadQueue,
            KnownCapabilities.ServiceDeskTicketCreate,
            KnownCapabilities.ServiceDeskTicketAssign,
            KnownCapabilities.ServiceDeskTicketMove,
            "paguai.draft.read",
            "paguai.draft.create",
        };
        var capabilities = new List<Capability>();
        foreach (var name in capabilityNames)
        {
            var capability = await administrator.Capabilities.SingleOrDefaultAsync(candidate =>
                candidate.Name == name
            );
            if (capability is null)
            {
                capability = name switch
                {
                    "paguai.draft.read" => new Capability(
                        Guid.Parse("766bdb56-80c0-5dee-982f-be7f0d3eaea8"),
                        name
                    ),
                    "paguai.draft.create" => new Capability(
                        Guid.Parse("aecaa296-6a6f-5815-93ac-bd1ccdff3f44"),
                        name
                    ),
                    _ => throw new InvalidOperationException($"Unknown capability {name}."),
                };
                administrator.Capabilities.Add(capability);
            }
            capabilities.Add(capability);
        }
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            actorMembership.Id,
            factory.ActorUserId,
            requestId
        );

        administrator.AddRange(
            group,
            company,
            assignee,
            actorMembership,
            assigneeMembership,
            role,
            assignment
        );
        administrator.RoleCapabilities.AddRange(
            capabilities.Select(capability => new RoleCapability(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                capability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            ))
        );
        await administrator.SaveChangesAsync();
        return new Scenario(company.Id, assigneeMembership.Id);
    }

    private sealed record Scenario(Guid CompanyId, Guid AssigneeMembershipId);

    private sealed record CreateCardRequest(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    );

    private sealed record CardResponse(
        Guid Id,
        Guid CompanyId,
        string Title,
        string Description,
        string Column,
        long Version,
        Guid? AssigneeMembershipId
    );
}
