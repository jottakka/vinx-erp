using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Provas da fronteira de decisão do estágio awaiting_approval (revisão item
/// 3): preparo por recurso permite duas demandas do MESMO ator aguardando
/// aprovação simultaneamente; a decisão exige a MESMA capability documentada
/// (demandas.demand.advance) dos dois lados — nem assign sozinho nem supervisor
/// nominal decidem — e a elegibilidade é resolvida DEPOIS da trava da linha da
/// demanda, sem janela TOCTOU para remoção de supervisão.
/// </summary>
public sealed class DemandasApprovalDecisionTests
{
    [Fact]
    public async Task Two_simultaneous_demands_of_one_actor_can_await_approval_together()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var first = await CreateDemandAsync(client, scenario);
        var second = await CreateDemandAsync(client, scenario);
        var firstAwaiting = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, first);
        var secondAwaiting = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, second);

        // O bug original: a segunda proposta viva do mesmo ator/empresa era
        // negada pela fronteira global; com a chave por recurso as duas coexistem
        // (AdvanceToAwaitingApprovalAsync já pinou stage + proposta pendente).
        Assert.NotEqual(firstAwaiting.PendingApprovalId, secondAwaiting.PendingApprovalId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var liveProposals = await administrator.Approvals
            .Where(approval => approval.CompanyId == scenario.CompanyId
                && approval.ActorUserId == factory.ActorUserId
                && approval.ConsumedAt == null)
            .ToListAsync();
        Assert.Equal(2, liveProposals.Count);
        Assert.Equal(2, liveProposals.Select(approval => approval.ResourceKey).Distinct().Count());
        Assert.All(liveProposals, approval => Assert.StartsWith("demandas.demand:", approval.ResourceKey));
    }

    [Fact]
    public async Task Creator_of_the_delivery_cannot_decide_the_own_demand()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var demand = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, await CreateDemandAsync(client, scenario));

        var result = await DecideAsync(
            factory,
            new ActorContext(factory.ActorUserId, Guid.NewGuid()),
            scenario,
            demand,
            approve: true);

        Assert.Equal(DemandOperationState.CapabilityDenied, result.State);
    }

    [Fact]
    public async Task Assign_only_actor_cannot_decide()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAsync(factory, includeAssignOnly: true);
        using var client = factory.CreateClient();
        var demand = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, await CreateDemandAsync(client, scenario));

        var result = await DecideAsync(
            factory,
            new ActorContext(scenario.AssignOnlyUserId!.Value, Guid.NewGuid()),
            scenario,
            demand,
            approve: true);

        Assert.Equal(DemandOperationState.CapabilityDenied, result.State);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            DemandStages.AwaitingApproval,
            (await administrator.Demands.SingleAsync(row => row.Id == demand.Id)).Stage);
    }

    [Fact]
    public async Task Supervisor_without_advance_capability_cannot_decide()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAsync(factory, includeSupervisorOnly: true);
        using var client = factory.CreateClient();
        var demand = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, await CreateDemandAsync(client, scenario));
        await AddSupervisorParticipantAsync(factory, scenario, demand.Id, scenario.SupervisorOnlyUserId!.Value);

        var result = await DecideAsync(
            factory,
            new ActorContext(scenario.SupervisorOnlyUserId!.Value, Guid.NewGuid()),
            scenario,
            demand,
            approve: true);

        Assert.Equal(DemandOperationState.CapabilityDenied, result.State);
    }

    [Fact]
    public async Task Advance_holder_decides_after_concurrent_supervisor_removal()
    {
        // A elegibilidade é resolvida DEPOIS da trava da demanda e depende da
        // capability documentada, não do papel nominal: remover a supervisão
        // antes da decisão não muda o desfecho do portador de advance, e o
        // ex-supervisor sem advance continua impedido (prova acima).
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAsync(factory, includeDecider: true);
        using var client = factory.CreateClient();
        var demand = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, await CreateDemandAsync(client, scenario));
        await AddSupervisorParticipantAsync(factory, scenario, demand.Id, scenario.DeciderUserId!.Value);

        // Supervisão removida em transação concorrente (antes da decisão).
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE demandas.demand_participants SET deleted_at = now(), deleted_by_user_id = {factory.ActorUserId}, deleted_reason = 'supervisor_removed' WHERE demand_id = {demand.Id} AND user_id = {scenario.DeciderUserId} AND deleted_at IS NULL""");

        var result = await DecideAsync(
            factory,
            new ActorContext(scenario.DeciderUserId!.Value, Guid.NewGuid()),
            scenario,
            demand,
            approve: true);

        Assert.Equal(DemandOperationState.Success, result.State);
        var stored = await administrator.Demands.AsNoTracking().SingleAsync(row => row.Id == demand.Id);
        Assert.Equal(DemandStages.Closed, stored.Stage);
        Assert.Null(stored.PendingApprovalId);
    }

    [Fact]
    public async Task Advance_holder_can_reject_returning_the_demand_to_execution()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAsync(factory, includeDecider: true);
        using var client = factory.CreateClient();
        var demand = await AdvanceToAwaitingApprovalAsync(factory, client, scenario, await CreateDemandAsync(client, scenario));

        var result = await DecideAsync(
            factory,
            new ActorContext(scenario.DeciderUserId!.Value, Guid.NewGuid()),
            scenario,
            demand,
            approve: false);

        Assert.Equal(DemandOperationState.Success, result.State);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var stored = await administrator.Demands.AsNoTracking().SingleAsync(row => row.Id == demand.Id);
        Assert.Equal(DemandStages.Execution, stored.Stage);
        Assert.Equal(demand.CycleCount + 1, stored.StageCycleCount);
        Assert.Null(stored.PendingApprovalId);
    }

    private static async Task<DemandOperationResult> DecideAsync(
        FoundationApiFactory factory,
        ActorContext actor,
        ApprovalScenario scenario,
        DemandAwaitingSeed demand,
        bool approve)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DemandasApprovalsService>();
        return await service.DecideAsync(
            actor,
            demand.Id,
            approve,
            new DemandasApprovalsService.DecisionCommand(scenario.CompanyId, Guid.NewGuid(), demand.Version));
    }

    /// <summary>
    /// Sem rotina o fluxo canônico é initiation → execution → awaiting_approval
    /// com gatilho explícito: dois avanços manuais do criador chegam à espera.
    /// </summary>
    private static async Task<DemandAwaitingSeed> AdvanceToAwaitingApprovalAsync(
        FoundationApiFactory factory,
        HttpClient client,
        ApprovalScenario scenario,
        DemandSeed demand)
    {
        DemandRowSnapshot current;
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            current = await ReadDemandAsync(administrator, demand.Id);
        }

        for (var advance = 0; advance < 2; advance++)
        {
            using var response = await client.PostAsJsonAsync(
                $"/demandas/demands/{demand.Id}/advance",
                new AdvanceStage(scenario.CompanyId, Guid.NewGuid(), current.Version));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var administrator = factory.Fixture.CreateAdministratorDbContext();
            current = await ReadDemandAsync(administrator, demand.Id);
        }

        Assert.Equal(DemandStages.AwaitingApproval, current.Stage);
        Assert.NotNull(current.PendingApprovalId);
        Assert.NotNull(current.PendingProposalId);
        return new DemandAwaitingSeed(demand.Id, current.Version, current.CycleCount, current.PendingApprovalId);
    }

    private static async Task<DemandRowSnapshot> ReadDemandAsync(FoundationDbContext administrator, Guid demandId)
    {
        var row = await administrator.Demands.AsNoTracking().SingleAsync(candidate => candidate.Id == demandId);
        return new DemandRowSnapshot(row.Stage, row.RowVersion, row.StageCycleCount, row.PendingApprovalId, row.PendingProposalId);
    }

    private static async Task AddSupervisorParticipantAsync(
        FoundationApiFactory factory,
        ApprovalScenario scenario,
        Guid demandId,
        Guid userId)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        // O criador já é o supervisor nato (uma linha viva por demanda): troca
        // a supervisão para o ator alvo antes de adicioná-lo.
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE demandas.demand_participants SET deleted_at = now(), deleted_by_user_id = {factory.ActorUserId}, deleted_reason = 'supervisor_replaced' WHERE demand_id = {demandId} AND role = 'supervisor' AND deleted_at IS NULL""");
        administrator.DemandParticipants.Add(new DemandParticipant
        {
            Id = Guid.NewGuid(),
            CompanyId = scenario.CompanyId,
            DemandId = demandId,
            UserId = userId,
            Role = DemandParticipantRoles.Supervisor,
            CreatedByUserId = factory.ActorUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await administrator.SaveChangesAsync();
    }

    private static async Task<DemandSeed> CreateDemandAsync(HttpClient client, ApprovalScenario scenario)
    {
        var requestId = Guid.NewGuid();
        using var response = await client.PostAsJsonAsync("/demandas/demands", new
        {
            companyId = scenario.CompanyId,
            requestId,
            title = "Aprovar contratação",
            description = "Revisar escopo antes de decidir.",
            priority = "medium",
            responsibleMembershipId = scenario.MembershipId,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var demand = await response.Content.ReadFromJsonAsync<DemandResponse>();
        Assert.NotNull(demand);
        return new DemandSeed(demand!.Id, demand.Version);
    }

    private static async Task<ApprovalScenario> SeedAsync(
        FoundationApiFactory factory,
        bool includeDecider = false,
        bool includeAssignOnly = false,
        bool includeSupervisorOnly = false)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Aprova", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Decisões", factory.ActorUserId, requestId);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        administrator.Groups.Add(group);
        administrator.Companies.Add(company);
        administrator.Memberships.Add(membership);

        // O criador precisa de create (criar a demanda) e advance (levá-la à espera).
        var creatorRole = new Role(Guid.NewGuid(), company.Id, "Avanço", factory.ActorUserId, requestId);
        administrator.Roles.Add(creatorRole);
        foreach (var capabilityName in new[] { KnownCapabilities.DemandasDemandCreate, KnownCapabilities.DemandasDemandAdvance })
        {
            var capability = await administrator.Capabilities.SingleAsync(row => row.Name == capabilityName);
            administrator.RoleCapabilities.Add(new RoleCapability(
                Guid.NewGuid(), company.Id, creatorRole.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId));
        }

        administrator.RoleAssignments.Add(new RoleAssignment(
            Guid.NewGuid(), company.Id, creatorRole.Id, membership.Id, factory.ActorUserId, requestId));

        Guid? deciderUserId = null;
        if (includeDecider)
        {
            deciderUserId = (await AddUserWithCapabilityAsync(
                administrator, factory, company.Id, "decide-demandas@example.test",
                KnownCapabilities.DemandasDemandAdvance, requestId)).UserId;
        }

        Guid? assignOnlyUserId = null;
        if (includeAssignOnly)
        {
            assignOnlyUserId = (await AddUserWithCapabilityAsync(
                administrator, factory, company.Id, "assign-only@example.test",
                KnownCapabilities.DemandasDemandAssign, requestId)).UserId;
        }

        Guid? supervisorOnlyUserId = null;
        if (includeSupervisorOnly)
        {
            supervisorOnlyUserId = (await AddUserWithCapabilityAsync(
                administrator, factory, company.Id, "supervisor-only@example.test",
                capabilityName: null, requestId)).UserId;
        }

        await administrator.SaveChangesAsync();

        return new ApprovalScenario(
            company.Id,
            membership.Id,
            deciderUserId,
            assignOnlyUserId,
            supervisorOnlyUserId);
    }

    private static async Task<(Guid UserId, Guid MembershipId)> AddUserWithCapabilityAsync(
        FoundationDbContext administrator,
        FoundationApiFactory factory,
        Guid companyId,
        string email,
        string? capabilityName,
        Guid requestId)
    {
        var user = new User(Guid.NewGuid(), email.Split('@')[0], email);
        var membership = new Membership(Guid.NewGuid(), companyId, user.Id, factory.ActorUserId, requestId);
        administrator.Users.Add(user);
        administrator.Memberships.Add(membership);
        if (capabilityName is not null)
        {
            var role = new Role(Guid.NewGuid(), companyId, $"Papel {email.Split('@')[0]}", factory.ActorUserId, requestId);
            var capability = await administrator.Capabilities.SingleAsync(row => row.Name == capabilityName);
            administrator.Roles.Add(role);
            administrator.RoleCapabilities.Add(new RoleCapability(
                Guid.NewGuid(), companyId, role.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId));
            administrator.RoleAssignments.Add(new RoleAssignment(
                Guid.NewGuid(), companyId, role.Id, membership.Id, factory.ActorUserId, requestId));
        }

        return (user.Id, membership.Id);
    }

    private sealed record ApprovalScenario(
        Guid CompanyId,
        Guid MembershipId,
        Guid? DeciderUserId,
        Guid? AssignOnlyUserId,
        Guid? SupervisorOnlyUserId);

    private sealed record DemandSeed(Guid Id, long Version);

    private sealed record DemandAwaitingSeed(Guid Id, long Version, int CycleCount, Guid? PendingApprovalId);

    private sealed record DemandRowSnapshot(
        string Stage,
        long Version,
        int CycleCount,
        Guid? PendingApprovalId,
        Guid? PendingProposalId);

    private sealed record AdvanceStage(Guid CompanyId, Guid RequestId, long ExpectedVersion);
}
