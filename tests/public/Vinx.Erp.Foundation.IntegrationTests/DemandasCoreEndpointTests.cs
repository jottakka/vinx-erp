extern alias workerhost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Wolverine;
using Xunit;
using WorkerAutomation = workerhost::Vinx.Erp.WorkerHost;
using WorkerProgram = workerhost::Program;

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
        var board = await response.Content.ReadFromJsonAsync<BoardPageContract>();
        Assert.NotNull(board);
        var card = board!.Items.Single(candidate => candidate.Id == demand.Id);
        Assert.True(card.IsOverdue);
    }

    /// <summary>
    /// Prova mínima end-to-end do tick de automação (runbook P3 item 4) DENTRO
    /// das fixtures existentes: WorkerHost real (DI fornece scan/handler/bus),
    /// fila durável demandas-automation materializada, varredura com identidade
    /// do worker avançando DUE primeiro e marcando overdue depois, e o handler
    /// auto-perpetuando o próximo tick na fila agendada.
    /// </summary>
    [Fact]
    public async Task Automation_tick_flows_through_durable_demandas_queue_due_first_and_self_perpetuates()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var scenario = await SeedScenarioAsync(factory, administrator);
        using var client = factory.CreateClient();

        var routineId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        administrator.DemandRoutines.Add(new DemandRoutine
        {
            Id = routineId,
            CompanyId = scenario.CompanyId,
            Name = "Ciclo com gatilho de tempo",
            Tipo = "Ciclo",
            Departamento = "Operações",
            RoutingOrder = 1,
            DefaultChecklistItems = [],
            IsActive = true,
            CreatedByUserId = factory.ActorUserId,
            UpdatedByUserId = factory.ActorUserId,
            RequestId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
        });
        await administrator.SaveChangesAsync();
        administrator.DemandRoutineStages.AddRange(
            new DemandRoutineStage { Id = Guid.NewGuid(), CompanyId = scenario.CompanyId, RoutineId = routineId, OrderIndex = 0, Stage = DemandStages.Initiation, TriggerKind = DemandStageTriggerKind.TimeElapsed, TriggerHours = 1m },
            new DemandRoutineStage { Id = Guid.NewGuid(), CompanyId = scenario.CompanyId, RoutineId = routineId, OrderIndex = 1, Stage = DemandStages.Execution, TriggerKind = DemandStageTriggerKind.ExplicitCompletion }
        );
        await administrator.SaveChangesAsync();

        // DUE: gatilho de tempo vencido (início da etapa 2h no passado).
        var dueDemand = await CreateInRoutineAsync(client, new RoutineScenario(scenario.CompanyId, scenario.MembershipId, routineId));
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE demandas.demands SET stage_started_at = now() - interval '2 hours' WHERE id = {dueDemand.Id}");
        // OVERDUE: sem rotina, prazo manual já vencido.
        var overdueDemand = await CreateDemandAsync(client, scenario, DateTimeOffset.UtcNow.AddHours(-1));

        await using var workerHost = new WorkerAutomationHostFactory(factory.Fixture.WorkerConnectionString);
        using var workerClient = workerHost.CreateClient();

        // Tick imediato pela fila durável (o envelope semeado pelo scheduler só
        // vence em +5min): o runtime do worker consome e executa o handler REAL.
        using (var busScope = workerHost.Services.CreateScope())
        {
            var bus = busScope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new WorkerAutomation.DemandasAutomationTick(DateTimeOffset.UtcNow));
        }

        var advanced = false;
        for (var attempt = 0; attempt < 40 && !advanced; attempt++)
        {
            await Task.Delay(500);
            advanced = await administrator.Database
                .SqlQuery<bool>($"""SELECT (stage = 'execution') AS "Value" FROM demandas.demands WHERE id = {dueDemand.Id}""")
                .SingleAsync();
        }
        Assert.True(advanced);

        var overdueMarked = await administrator.Database
            .SqlQuery<bool>($"""SELECT (overdue_since IS NOT NULL) AS "Value" FROM demandas.demands WHERE id = {overdueDemand.Id}""")
            .SingleAsync();
        Assert.True(overdueMarked);

        // Fila durável drenada: o envelope do tick foi consumido pelo listener
        // (o handler auto-perpetua agendando o próximo tick ao final de cada
        // execução; encadeamentos concorrentes tornam a contagem da fila
        // agendada intrinsecamente corrida, então ela não é pinada aqui).
        await Task.Delay(1000);
        var queuedTicks = await administrator.Database
            .SqlQuery<int>($"""SELECT count(*) AS "Value" FROM vinx_wolverine_queues.wolverine_queue_demandas_automation""")
            .SingleAsync();
        Assert.True(queuedTicks == 0);

        // DUE primeiro/overdue depois: provado com UMA varredura direta e
        // determinística do serviço real (identidade do worker), sem a
        // concorrência dos ticks enfileirados pelo Wolverine.
        var secondDue = await CreateInRoutineAsync(client, new RoutineScenario(scenario.CompanyId, scenario.MembershipId, routineId));
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE demandas.demands SET stage_started_at = now() - interval '2 hours' WHERE id = {secondDue.Id}");
        var secondOverdue = await CreateDemandAsync(client, scenario, DateTimeOffset.UtcNow.AddHours(-1));

        var workerOptions = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(factory.Fixture.WorkerConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
            .Options;
        await using var workerDatabase = new FoundationDbContext(workerOptions);
        var scan = new DemandasAutomationScanService(
            workerDatabase,
            new DemandasNotificationComposer(
                new NotificationIntentService(workerDatabase, null!, NullLogger<NotificationIntentService>.Instance),
                NullLogger<DemandasNotificationComposer>.Instance),
            NullLogger<DemandasAutomationScanService>.Instance);
        await scan.ScanAsync(CancellationToken.None);

        var scanEvents = await administrator.Database
            .SqlQuery<string>(
                $"""SELECT kind || '@' || extract(epoch from occurred_at)::bigint || '#' || left(demand_id::text, 8) AS "Value" FROM demandas.demand_events WHERE demand_id IN ({secondDue.Id}, {secondOverdue.Id}) AND kind IN ('stage_advanced', 'overdue') ORDER BY occurred_at"""
            )
            .ToListAsync();
        Assert.Equal(
            [$"stage_advanced#{secondDue.Id.ToString()[..8]}", $"overdue#{secondOverdue.Id.ToString()[..8]}"],
            scanEvents.Select(row => row.Split('@')[0] + row[row.IndexOf('#')..]).ToList()
        );
    }

    private sealed class WorkerAutomationHostFactory(string? connectionString)
        : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FOUNDATION_WORKER_CONNECTION", connectionString ?? string.Empty);
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.test/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
            builder.UseSetting("SUPABASE_AUTH_SERVICE_ROLE_KEY", "test-service-role-key");
            builder.UseSetting("SUPABASE_INVITE_REDIRECT_URL", "https://app.example.test/convite");
        }
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
        // O modelo não declara a relação stage→routine (FK só no DDL das
        // migrações SQL): salvar ambos no mesmo batch deixa o EF sem ordenar
        // o pai antes do filho e quebra fk_demand_routine_stages_routine.
        administrator.DemandRoutines.Add(routine);
        await administrator.SaveChangesAsync();
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

    private sealed record BoardPageContract(
        IReadOnlyList<DemandBoardCardContract> Items,
        int Total,
        bool HasMore,
        string? NextCursor
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
