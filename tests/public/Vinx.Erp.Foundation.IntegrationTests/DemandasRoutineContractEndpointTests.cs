using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Contrato de rotinas Demandas e corrida de idempotência: etapas persistidas e
/// devolvidas nas respostas, topologia sem caminho operacional rejeitada,
/// auto-atribuição validada e replay/conflito no lugar de 503.
/// </summary>
public sealed class DemandasRoutineContractEndpointTests
{
    private static readonly string[] CoreCapabilities =
    [
        KnownCapabilities.DemandasDemandRead,
        KnownCapabilities.DemandasDemandCreate,
        KnownCapabilities.DemandasRoutineManage,
    ];

    [Fact]
    public async Task Creating_a_routine_persists_stages_and_replays_the_full_response()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        var created = await CreateRoutineAsync(client, scenario, requestId);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var routine = await created.Content.ReadFromJsonAsync<RoutineResponse>();
        Assert.NotNull(routine);
        Assert.Equal(2, routine!.Stages.Count);
        Assert.Equal("initiation", routine.Stages[0].Stage);
        Assert.Equal("execution", routine.Stages[1].Stage);

        using var replay = await CreateRoutineAsync(client, scenario, requestId);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<RoutineResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(routine.Id, replayed!.Id);
        Assert.Equal(2, replayed.Stages.Count);
        Assert.Equal(routine.Stages[1].Id, replayed.Stages[1].Id);

        var list = await client.GetFromJsonAsync<RoutineResponse[]>(
            $"/demandas/routines?companyId={scenario.CompanyId}");
        Assert.NotNull(list);
        var listed = Assert.Single(list!, candidate => candidate.Id == routine.Id);
        Assert.Equal(2, listed.Stages.Count);
    }

    [Fact]
    public async Task Creating_a_routine_with_time_elapsed_into_awaiting_approval_is_rejected()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();

        using var rejected = await client.PostAsJsonAsync("/demandas/routines", new
        {
            companyId = scenario.CompanyId,
            requestId = Guid.NewGuid(),
            name = "Topologia presa",
            stages = new object[]
            {
                new { orderIndex = 0, stage = "initiation", triggerKind = 1, triggerHours = (decimal?)null, deadlineHours = (decimal?)null },
                new { orderIndex = 1, stage = "execution", triggerKind = 3, triggerHours = 1m, deadlineHours = (decimal?)null },
                new { orderIndex = 2, stage = "awaiting_approval", triggerKind = 1, triggerHours = (decimal?)null, deadlineHours = (decimal?)null },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // Vizinho válido: entrada manual em aprovação continua aceita.
        using var accepted = await client.PostAsJsonAsync("/demandas/routines", new
        {
            companyId = scenario.CompanyId,
            requestId = Guid.NewGuid(),
            name = "Topologia viável",
            stages = new object[]
            {
                new { orderIndex = 0, stage = "initiation", triggerKind = 1, triggerHours = (decimal?)null, deadlineHours = (decimal?)null },
                new { orderIndex = 1, stage = "awaiting_approval", triggerKind = 1, triggerHours = (decimal?)null, deadlineHours = (decimal?)null },
            },
        });
        Assert.True(
            accepted.StatusCode == HttpStatusCode.Created,
            await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_identical_demand_creates_replay_instead_of_failing_unavailable()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        async Task<HttpResponseMessage> Create() =>
            await client.PostAsJsonAsync("/demandas/demands", new
            {
                companyId = scenario.CompanyId,
                requestId,
                title = "Corrida de idempotência",
                description = "Duas requisições idênticas concorrentes.",
                priority = "medium",
                responsibleMembershipId = scenario.MembershipId,
            });

        var responses = await Task.WhenAll(Create(), Create());
        var statuses = responses.Select(response => (int)response.StatusCode).ToArray();
        Assert.DoesNotContain(503, statuses);
        Assert.Contains(201, statuses);

        var first = await responses[0].Content.ReadFromJsonAsync<JsonElement>();
        var second = await responses[1].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            first.GetProperty("id").GetGuid(),
            second.GetProperty("id").GetGuid());

        var demands = await administrator.Demands.AsNoTracking()
            .CountAsync(demand => demand.CompanyId == scenario.CompanyId && demand.RequestId == requestId);
        Assert.Equal(1, demands);
        var receipts = await administrator.IdempotencyRecords.AsNoTracking()
            .CountAsync(record => record.CompanyId == scenario.CompanyId && record.RequestId == requestId);
        Assert.Equal(1, receipts);
    }

    [Fact]
    public async Task Concurrent_identical_routine_creates_replay_the_complete_stages()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        var responses = await Task.WhenAll(
            CreateRoutineAsync(client, scenario, requestId),
            CreateRoutineAsync(client, scenario, requestId));
        var statuses = responses.Select(response => response.StatusCode).ToArray();
        Assert.DoesNotContain(HttpStatusCode.ServiceUnavailable, statuses);
        Assert.Contains(HttpStatusCode.Created, statuses);
        Assert.Contains(HttpStatusCode.OK, statuses);

        var first = await responses[0].Content.ReadFromJsonAsync<RoutineResponse>();
        var second = await responses[1].Content.ReadFromJsonAsync<RoutineResponse>();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(first.Stages.Select(stage => stage.Id), second.Stages.Select(stage => stage.Id));
        Assert.Equal(2, first.Stages.Count);

        Assert.Equal(1, await administrator.DemandRoutines.CountAsync(
            routine => routine.CompanyId == scenario.CompanyId));
        Assert.Equal(2, await administrator.DemandRoutineStages.CountAsync(
            stage => stage.CompanyId == scenario.CompanyId));
        Assert.Equal(1, await administrator.IdempotencyRecords.CountAsync(
            record => record.RequestId == requestId));
    }

    [Fact]
    public async Task Divergent_replay_of_the_same_request_returns_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        using var first = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId,
            title = "Original",
            description = "Primeira carga.",
            priority = "medium",
            responsibleMembershipId = scenario.MembershipId,
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var divergent = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId,
            title = "Divergente",
            description = "Mesmo requestId, carga diferente.",
            priority = "medium",
            responsibleMembershipId = scenario.MembershipId,
        });
        Assert.Equal(HttpStatusCode.Conflict, divergent.StatusCode);
        var body = await divergent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_conflict", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Demand_without_explicit_responsible_resolves_the_routine_auto_assignee()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var routineId = await CreateRoutineAsync(
            client, scenario, Guid.NewGuid(), autoAssigneeMembershipId: scenario.OtherMembershipId, expectCreated: true).ContinueWith(
            task => task.Result.Content.ReadFromJsonAsync<RoutineResponse>().Result!.Id);

        // Sem responsável: rotina roteada explicitamente resolve o auto-atribuído.
        using var created = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId = Guid.NewGuid(),
            title = "Auto-atribuída",
            description = "Sem responsável explícito.",
            priority = "medium",
            routineId,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var demand = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(scenario.OtherMembershipId, demand.GetProperty("responsibleMembershipId").GetGuid());

        // Responsável explícito vence a rotina.
        using var explicitWinner = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId = Guid.NewGuid(),
            title = "Explícito vence",
            description = "Responsável explícito prevalece.",
            priority = "medium",
            responsibleMembershipId = scenario.MembershipId,
            routineId,
        });
        Assert.Equal(HttpStatusCode.Created, explicitWinner.StatusCode);
        var explicitDemand = await explicitWinner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(scenario.MembershipId, explicitDemand.GetProperty("responsibleMembershipId").GetGuid());

        // Sem responsável e sem auto-atribuído → 400, sem fallback silencioso.
        var routineWithoutAssigneeId = await CreateRoutineAsync(
            client, scenario, Guid.NewGuid(), autoAssigneeMembershipId: null, expectCreated: true).ContinueWith(
            task => task.Result.Content.ReadFromJsonAsync<RoutineResponse>().Result!.Id);
        using var rejected = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId = Guid.NewGuid(),
            title = "Sem responsável possível",
            description = "Sem responsável e sem auto-atribuído.",
            priority = "medium",
            routineId = routineWithoutAssigneeId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Demandas_responses_carry_no_store_cache_control_for_bearer_clients()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();

        using var board = await client.GetAsync($"/demandas/board?companyId={scenario.CompanyId}");
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        Assert.Contains("no-store", board.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> CreateRoutineAsync(
        HttpClient client,
        Scenario scenario,
        Guid requestId,
        Guid? autoAssigneeMembershipId = null,
        bool expectCreated = false
    )
    {
        var response = await client.PostAsJsonAsync("/demandas/routines", new
        {
            companyId = scenario.CompanyId,
            requestId,
            name = $"Rotina {requestId.ToString("N")[..20]}",
            autoAssigneeMembershipId,
            routingOrder = 10,
            stages = new object[]
            {
                new { orderIndex = 0, stage = "initiation", triggerKind = 1, triggerHours = (decimal?)null, deadlineHours = (decimal?)null },
                new { orderIndex = 1, stage = "execution", triggerKind = 1, triggerHours = (decimal?)null, deadlineHours = (decimal?)null },
            },
        });
        if (expectCreated)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return response;
    }

    private static async Task<Scenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        FoundationDbContext administrator
    )
    {
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Demandas", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Fluxo", factory.ActorUserId, requestId);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var colleagueUser = new User(Guid.NewGuid(), "Bea", "bea-routine@example.test");
        var otherMembership = new Membership(Guid.NewGuid(), company.Id, colleagueUser.Id, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Demandas admin", factory.ActorUserId, requestId);
        var capabilities = await administrator.Capabilities.Where(capability => CoreCapabilities.Contains(capability.Name)).ToArrayAsync();
        Assert.Equal(CoreCapabilities.Length, capabilities.Length);
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, requestId);

        administrator.AddRange(group, company, colleagueUser, membership, otherMembership, role, assignment);
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

        return new Scenario(company.Id, membership.Id, otherMembership.Id);
    }

    private sealed record Scenario(Guid CompanyId, Guid MembershipId, Guid OtherMembershipId);
}
