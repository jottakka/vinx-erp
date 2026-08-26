using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Provas de integração do núcleo Demandas. Executam na janela de gate
/// (Testcontainers); nesta fatia o compromisso é compilar limpo e fixar o
/// contrato público verbatim.
/// </summary>
public sealed class DemandasCoreEndpointTests
{
    private static readonly string[] CoreCapabilities =
    [
        KnownCapabilities.DemandasDemandRead,
        KnownCapabilities.DemandasDemandCreate,
        KnownCapabilities.DemandasDemandAdvance,
        KnownCapabilities.DemandasDemandAssign,
        KnownCapabilities.DemandasParticipantsManage,
        KnownCapabilities.DemandasRoutineManage,
    ];

    [Fact]
    public async Task Creating_a_demand_enters_initiation_and_replays_the_same_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync("/demandas/demands", CreatePayloadBody(scenario));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<DemandResponseContract>();
        Assert.NotNull(created);
        Assert.Equal("initiation", created!.Stage);
        Assert.Equal(1, created.Version);
        Assert.False(created.IsOverdue);

        using var replay = await client.PostAsJsonAsync("/demandas/demands", CreatePayloadBody(scenario));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<DemandResponseContract>();
        Assert.NotNull(replayed);
        Assert.Equal(created.Id, replayed!.Id);
        Assert.Equal(created.Version, replayed.Version);
    }

    [Fact]
    public async Task Advance_with_wrong_version_returns_conflict_and_keeps_stage()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var demand = await CreateDemandAsync(client, scenario);

        using var conflict = await client.PostAsJsonAsync(
            $"/demandas/demands/{demand.Id}/advance",
            new AdvanceStage(scenario.CompanyId, Guid.NewGuid(), 99)
        );

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        var stored = await verify.Demands.SingleAsync(row => row.Id == demand.Id);
        Assert.Equal("initiation", stored.Stage);
    }

    [Fact]
    public async Task Reassign_by_creator_reinitiates_flow_and_bumps_cycle_count()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var demand = await CreateDemandAsync(client, scenario);

        using var reassign = await client.PostAsJsonAsync(
            $"/demandas/demands/{demand.Id}/responsible",
            new ReassignResponsible(scenario.CompanyId, Guid.NewGuid(), demand.Version, scenario.OtherMembershipId)
        );

        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);
        var updated = await reassign.Content.ReadFromJsonAsync<DemandResponseContract>();
        Assert.NotNull(updated);
        Assert.Equal("initiation", updated!.Stage);
        Assert.True(updated.CycleCount >= 2);
        Assert.True(updated.Version > demand.Version);
    }

    [Fact]
    public async Task Manual_advance_is_illegal_for_checklist_triggered_stage()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedRoutineScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var demand = await CreateInRoutineAsync(client, scenario);

        using var advance = await client.PostAsJsonAsync(
            $"/demandas/demands/{demand.Id}/advance",
            new AdvanceStage(scenario.CompanyId, Guid.NewGuid(), demand.Version)
        );

        Assert.Equal(HttpStatusCode.Conflict, advance.StatusCode);
        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        var stored = await verify.Demands.SingleAsync(row => row.Id == demand.Id);
        Assert.Equal(DemandStages.Initiation, stored.Stage);
    }

    [Fact]
    public async Task Board_derives_overdue_from_past_effective_deadline()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();
        var demand = await CreateDemandAsync(
            client,
            scenario,
            manualDueAt: DateTimeOffset.UtcNow.AddHours(-2)
        );

        using var response = await client.GetAsync($"/demandas/board?companyId={scenario.CompanyId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var board = await response.Content.ReadFromJsonAsync<List<DemandBoardCardContract>>();
        Assert.NotNull(board);
        var card = board!.Single(candidate => candidate.Id == demand.Id);
        Assert.True(card.IsOverdue);
    }

    [Fact]
    public async Task Reader_without_membership_in_company_is_forbidden()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();

        var foreignGroup = new Group(Guid.NewGuid(), "Grupo isolado", factory.ActorUserId, Guid.NewGuid());
        var foreignCompany = new Company(Guid.NewGuid(), foreignGroup.Id, "Empresa sem vínculo", factory.ActorUserId, Guid.NewGuid());
        administrator.Groups.Add(foreignGroup);
        administrator.Companies.Add(foreignCompany);
        await administrator.SaveChangesAsync();

        using var response = await client.GetAsync($"/demandas/board?companyId={foreignCompany.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static object CreatePayloadBody(Scenario scenario) =>
        new
        {
            companyId = scenario.CompanyId,
            requestId = scenario.RequestId,
            title = "Acesso bloqueado",
            description = "Portal trava ao entrar.",
            priority = "high",
            responsibleMembershipId = scenario.MembershipId,
            manualDueAt = (DateTimeOffset?)null,
        };

    private static async Task<DemandSeed> CreateDemandAsync(
        HttpClient client,
        Scenario scenario,
        DateTimeOffset? manualDueAt = null
    )
    {
        var requestId = Guid.NewGuid();
        using var response = await client.PostAsJsonAsync(
            "/demandas/demands",
            new
            {
                companyId = scenario.CompanyId,
                requestId,
                title = "Acesso bloqueado",
                description = "Portal trava ao entrar.",
                priority = "high",
                responsibleMembershipId = scenario.MembershipId,
                manualDueAt,
            }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var demand = await response.Content.ReadFromJsonAsync<DemandResponse>();
        Assert.NotNull(demand);
        return new DemandSeed(demand!.Id, demand.Version);
    }

private static async Task<DemandSeed> CreateInRoutineAsync(HttpClient client, RoutineScenario scenario)
    {
        var requestId = Guid.NewGuid();
        using var response = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId,
            title = "Fechamento contábil",
            description = "Conciliar contas do período.",
            priority = "medium",
            responsibleMembershipId = scenario.MembershipId,
            routineId = scenario.RoutineId,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var demand = await response.Content.ReadFromJsonAsync<DemandResponse>();
        Assert.NotNull(demand);
        return new DemandSeed(demand!.Id, demand.Version);
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
        var colleagueUser = new User(Guid.NewGuid(), "Bea", "bea-demandas@example.test");
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

        return new Scenario(group.Id, company.Id, membership.Id, otherMembership.Id, Guid.NewGuid());
    }

    private static async Task<RoutineScenario> SeedRoutineScenarioAsync(
        FoundationApiFactory factory,
        FoundationDbContext administrator
    )
    {
        var baseScenario = await SeedScenarioAsync(factory, administrator);
        var now = DateTimeOffset.UtcNow;
        var routine = new DemandRoutine
        {
            Id = Guid.NewGuid(),
            CompanyId = baseScenario.CompanyId,
            Name = "Fechamento mensal",
            Description = null,
            Tipo = "Fechamento",
            Departamento = "Contábil",
            AutoAssigneeMembershipId = null,
            RoutingOrder = 10,
            DefaultChecklistItems = ["Importar movimento"],
            IsActive = true,
            CreatedByUserId = factory.ActorUserId,
            UpdatedByUserId = factory.ActorUserId,
            RequestId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
        };
        administrator.DemandRoutines.Add(routine);
        administrator.DemandRoutineStages.AddRange(
            new DemandRoutineStage { Id = Guid.NewGuid(), CompanyId = baseScenario.CompanyId, RoutineId = routine.Id, OrderIndex = 0, Stage = DemandStages.Initiation, TriggerKind = DemandStageTriggerKind.ChecklistCompleted },
            new DemandRoutineStage { Id = Guid.NewGuid(), CompanyId = baseScenario.CompanyId, RoutineId = routine.Id, OrderIndex = 1, Stage = DemandStages.Execution, TriggerKind = DemandStageTriggerKind.ExplicitCompletion }
        );
        await administrator.SaveChangesAsync();
        return new RoutineScenario(baseScenario.CompanyId, baseScenario.MembershipId, routine.Id);
    }

    private sealed record Scenario(
        Guid GroupId,
        Guid CompanyId,
        Guid MembershipId,
        Guid OtherMembershipId,
        Guid RequestId
    );

    private sealed record RoutineScenario(Guid CompanyId, Guid MembershipId, Guid RoutineId);

    private sealed record DemandSeed(Guid Id, long Version);

    private sealed record AdvanceStage(Guid CompanyId, Guid RequestId, long ExpectedVersion);

    private sealed record ReassignResponsible(Guid CompanyId, Guid RequestId, long ExpectedVersion, Guid NewResponsibleMembershipId);

    private sealed record DemandResponseContract(
        Guid Id,
        string Stage,
        int CycleCount,
        bool IsOverdue,
        long Version
    );

    private sealed record DemandBoardCardContract(
        Guid Id,
        bool IsOverdue,
        string Stage
    );

#pragma warning disable IDE0051 // Payload tipados usados pelos testes via JSON anônimo; tipos mantidos para clareza de contrato.
    private sealed record CreateDemand(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description,
        string Priority,
        Guid ResponsibleMembershipId
    );
#pragma warning restore IDE0051
}
