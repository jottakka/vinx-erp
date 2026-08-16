using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AtendimentoCardEndpointTests
{
    [Fact]
    public async Task Creating_a_card_returns_created_and_replays_the_same_request_without_duplication()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = new CreateCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            "Acesso bloqueado",
            "Usuário não consegue entrar no portal."
        );

        using var first = await client.PostAsJsonAsync("/atendimento/cards", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(created);
        Assert.Equal(scenario.CompanyId, created.CompanyId);
        Assert.Equal(request.Title, created.Title);
        Assert.Equal(request.Description, created.Description);
        Assert.Equal("novo", created.Column);
        Assert.Equal(1, created.Version);

        using var replay = await client.PostAsJsonAsync("/atendimento/cards", request);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(created.Version, replayed.Version);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == request.RequestId
            )
        );
    }

    [Fact]
    public async Task Reusing_a_create_request_with_different_payload_returns_idempotency_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        using var first = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var conflict = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Título alterado",
                "Mesmo request_id com payload diferente."
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var error = await conflict.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("idempotency_conflict", error?.Code);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
    }

    [Fact]
    public async Task A_stale_move_returns_conflict_without_overwriting_the_current_column()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var moved = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, "em_andamento")
        );
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        using var stale = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, "concluido")
        );
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var queue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var cards = await queue.Content.ReadFromJsonAsync<List<CardResponse>>();
        var current = Assert.Single(cards!, card => card.Id == created.Id);
        Assert.Equal("em_andamento", current.Column);
        Assert.Equal(created.Version + 1, current.Version);
    }

    [Fact]
    public async Task Assigning_an_active_membership_updates_the_card_and_replays_the_same_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var request = new AssignCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            created.Version,
            scenario.AssigneeMembershipId
        );

        using var first = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            request
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var assigned = await first.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(assigned);
        Assert.Equal(scenario.AssigneeMembershipId, assigned.AssigneeMembershipId);
        Assert.Equal(created.Version + 1, assigned.Version);

        using var replay = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            request
        );

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(assigned.Id, replayed.Id);
        Assert.Equal(assigned.Version, replayed.Version);
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

    private static async Task<AtendimentoScenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Atendimento",
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
            "Atendimento administrator",
            factory.ActorUserId,
            requestId
        );
        var capabilities = await administrator
            .Capabilities.Where(capability => capability.Name.StartsWith("service_desk.ticket."))
            .ToArrayAsync();
        Assert.Equal(4, capabilities.Length);
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
        return new AtendimentoScenario(company.Id, assigneeMembership.Id);
    }

    private sealed record AtendimentoScenario(Guid CompanyId, Guid AssigneeMembershipId);

    private sealed record CreateCardRequest(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    );

    private sealed record MoveCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        string Column
    );

    private sealed record AssignCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        Guid AssigneeMembershipId
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

    private sealed record ErrorResponse(string Code);
}
