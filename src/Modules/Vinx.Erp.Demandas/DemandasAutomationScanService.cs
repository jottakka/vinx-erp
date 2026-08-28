using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Demandas;

/// <summary>
/// Varredura operacional das automações de demanda: PRIMEIRO avança gatilhos de
/// tempo vencidos, DEPOIS marca atraso derivado (ordem determinística do mesmo
/// tick). Cada empresa é UMA transação explícita: set_config vive dentro dela
/// (set_config(...,true) morre com a transação — nunca solto), lotes FOR UPDATE
/// SKIP LOCKED e eventos append-only idempotentes por chave (demanda, etapa,
/// gatilho). Avisos saem pelo compositor APÓS o commit (terminal-only).
/// Corta com decisão documentada: NÃO avança automaticamente para
/// awaiting_approval porque o preparo exige ator com capability.
/// </summary>
public sealed class DemandasAutomationScanService(
    FoundationDbContext database,
    DemandasNotificationComposer notifications,
    ILogger<DemandasAutomationScanService> logger
)
{
    private const string Module = "demandas";
    private const string Operation = "demandas_automation_scan";
    private const int BatchSize = 200;

    private sealed record PendingNotes(Guid DemandId, int Cycle, string Kind, Audience Recipients);

    /// <summary>Destinatários e o solicitante da intenção (responsável vigente).</summary>
    private sealed record Audience(Guid RequesterUserId, IReadOnlyList<Guid> Users);

    /// <summary>Ator sintético do worker (sem PII; só alimenta GUC/correlação).</summary>
    private static readonly ActorContext SystemActor = new(Guid.Empty, Guid.NewGuid());

    public async Task<DemandasAutomationSummary> ScanAsync(CancellationToken cancellationToken)
    {
        // Descoberta via função SECURITY DEFINER do dono dedicado: a tabela é
        // FORCE RLS e o worker não tem GUC aqui — consulta direta sempre é 0 linhas.
        var companies = await database.Database
            .SqlQuery<Guid>($"SELECT company_id AS \"Value\" FROM demandas.enumerate_automation_companies()")
            .ToListAsync(cancellationToken);

        var advanced = 0;
        var markedOverdue = 0;
        foreach (var companyId in companies)
        {
            List<PendingNotes> pending = [];
            try
            {
                await using var unit = await database.Database.BeginTransactionAsync(cancellationToken);
                await SetCompanyScopeAsync(companyId, cancellationToken);
                pending.AddRange(await AdvanceDueStagesAsync(companyId, cancellationToken));
                pending.AddRange(await MarkOverdueAsync(companyId, cancellationToken));
                await database.SaveChangesAsync(cancellationToken);
                await unit.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                database.ChangeTracker.Clear();
                logger.LogError(
                    exception,
                    "Demandas {Operation} {Module} {Result} {Reason} {CompanyId}",
                    Operation, Module, "unavailable", "company_batch_failed", companyId
                );
                continue;
            }

            // Terminal-only: avisos somente depois do commit da unidade.
            foreach (var note in pending)
            {
                await SafeNotifyAsync(companyId, note.DemandId, note.Cycle, note.Kind, note.Recipients, cancellationToken);
            }

            advanced += pending.Count(note => note.Kind == DemandNotificationKinds.StageAdvanced);
            markedOverdue += pending.Count(note => note.Kind == DemandNotificationKinds.Overdue);
        }

        if (advanced > 0 || markedOverdue > 0)
        {
            logger.LogInformation(
                "Demandas {Operation} {Module} {Result} {Advanced} {Overdue}",
                Operation, Module, "succeeded", advanced, markedOverdue
            );
        }

        return new DemandasAutomationSummary(companies.Count, advanced, markedOverdue);
    }

    /// <summary>Lotes mutam entidades rastreadas; SaveChanges fica na unidade.</summary>
    private async Task<List<PendingNotes>> AdvanceDueStagesAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var candidates = await database
            .Demands.FromSqlInterpolated(
                $"""
                SELECT d.* FROM demandas.demands d
                JOIN demandas.demand_routine_stages s
                  ON s.company_id = d.company_id AND s.routine_id = d.routine_id AND s.stage = d.stage
                WHERE d.company_id = {companyId}
                  AND d.deleted_at IS NULL AND d.closed_at IS NULL
                  AND NOT d.automation_paused
                  AND s.trigger_kind = {unchecked((int)DemandStageTriggerKind.TimeElapsed)}
                  AND s.trigger_hours IS NOT NULL
                  AND now() >= coalesce(d.stage_started_at, d.updated_at) + (s.trigger_hours * interval '1 hour')
                ORDER BY d.id
                LIMIT {BatchSize}
                FOR UPDATE OF d SKIP LOCKED
                """
            )
            .ToListAsync(cancellationToken);

        List<PendingNotes> pending = [];
        foreach (var demand in candidates)
        {
            var stageRules = await LoadStageRulesAsync(demand.CompanyId, demand.RoutineId, cancellationToken);
            var fromStage = demand.Stage;
            var toStage = DemandRules.ResolveNextStage(stageRules, fromStage);
            if (toStage == DemandStages.AwaitingApproval)
            {
                // Preparo de aprovação precisa de ator com capability; fica para
                // o responsável clicar (avanço manual) — registrado por demanda.
                logger.LogInformation(
                    "Demandas {Operation} {Module} {Result} {Reason} {DemandId}",
                    Operation, Module, "skipped", "awaiting_approval_needs_actor", demand.Id
                );
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            AppendSystemEvent(
                demand,
                "stage_advanced",
                $"time:{demand.StageCycleCount}:{fromStage}:{toStage}:{now.UtcTicks}",
                now,
                fromStage,
                toStage
            );
            demand.Stage = toStage;
            demand.StageStartedAt = now;
            demand.StageDueAt = ComputeDeadlineFor(stageRules, toStage, now);
            demand.OverdueSince = null;
            if (toStage == DemandStages.Closed)
            {
                demand.ClosedAt = now;
            }

            MarkRowVersion(demand);
            pending.Add(new(demand.Id, demand.StageCycleCount, DemandNotificationKinds.StageAdvanced, await CollectAudienceAsync(demand, cancellationToken)));
        }

        return pending;
    }

    private async Task<List<PendingNotes>> MarkOverdueAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await database
            .Demands.FromSqlInterpolated(
                $"""
                SELECT * FROM demandas.demands
                WHERE company_id = {companyId}
                  AND deleted_at IS NULL AND closed_at IS NULL
                  AND NOT automation_paused
                  AND overdue_since IS NULL
                  AND LEAST(COALESCE(manual_due_at, 'infinity'::timestamptz),
                            COALESCE(sla_due_at, 'infinity'::timestamptz),
                            COALESCE(stage_due_at, 'infinity'::timestamptz)) < now()
                ORDER BY id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """
            )
            .ToListAsync(cancellationToken);

        List<PendingNotes> pending = [];
        foreach (var demand in candidates)
        {
            // Avanços do passo 1 no mesmo tick podem ter renovado os prazos:
            // marca apenas quem ainda está vencido após eles.
            var effective = EffectiveDeadlineOf(demand);
            if (!effective.HasValue || effective.Value >= now)
            {
                continue;
            }

            demand.OverdueSince = now;
            AppendSystemEvent(demand, "overdue", $"overdue:{demand.StageCycleCount}:{effective.Value.UtcTicks}", now);
            MarkRowVersion(demand);
            pending.Add(new(demand.Id, demand.StageCycleCount, DemandNotificationKinds.Overdue, await CollectAudienceAsync(demand, cancellationToken)));
        }

        return pending;
    }

    private void MarkRowVersion(Demand demand)
    {
        // Raiz tocada manualmente (o interceptor avança Modified entries): fixa
        // expected=original+1 determinístico para conflito otimista real.
        var entry = database.Entry(demand).Property(row => row.RowVersion);
        entry.CurrentValue = entry.OriginalValue + 1;
        entry.IsModified = true;
    }

    private static DateTimeOffset? EffectiveDeadlineOf(Demand demand) =>
        DemandRules.EffectiveDeadline(demand.ManualDueAt, demand.SlaDueAt, demand.StageDueAt);

    private void AppendSystemEvent(
        Demand demand,
        string kind,
        string occurrenceToken,
        DateTimeOffset at,
        string? fromStage = null,
        string? toStage = null
    ) =>
        database.DemandEvents.Add(new DemandEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = demand.CompanyId,
            DemandId = demand.Id,
            Kind = kind,
            FromStage = fromStage,
            ToStage = toStage,
            CycleCount = demand.StageCycleCount,
            OccurrenceToken = occurrenceToken,
            ActorUserId = null,
            MetadataJson = JsonSerializer.Serialize(new { source = "worker_tick" }),
            OccurredAt = at,
        });

    private async Task<List<DemandStageRule>> LoadStageRulesAsync(Guid companyId, Guid? routineId, CancellationToken cancellationToken)
    {
        if (routineId is not { } routine)
        {
            return [];
        }

        return await database
            .DemandRoutineStages.AsNoTracking()
            .Where(stage => stage.CompanyId == companyId && stage.RoutineId == routine)
            .OrderBy(stage => stage.OrderIndex)
            .Select(stage => new DemandStageRule(stage.Id, stage.OrderIndex, stage.Stage, stage.TriggerKind, stage.TriggerHours, stage.DeadlineHours))
            .ToListAsync(cancellationToken);
    }

    private static DateTimeOffset? ComputeDeadlineFor(List<DemandStageRule> stageRules, string stage, DateTimeOffset from)
    {
        var hours = stageRules.FirstOrDefault(rule => rule.Stage == stage)?.DeadlineHours;
        return hours.HasValue ? from.AddHours((double)hours.Value) : null;
    }

    private async Task<Audience> CollectAudienceAsync(Demand demand, CancellationToken cancellationToken)
    {
        // Responsável resolvido pela função do dono dedicado (memberships estão
        // fora dos grants diretos do worker); observadores leem da própria
        // tabela. O responsável também é o solicitante da intenção: a função
        // worker-only exige ator real (created_by_user_id NOT NULL) — sem
        // responsável, os avisos caem no best-effort com ator sintético e
        // falham fechado na função.
        var responsibleUser = (await database.Database
            .SqlQuery<Guid?>($"SELECT demand_responsible_user AS \"Value\" FROM demandas.demand_responsible_user({demand.Id})")
            .SingleOrDefaultAsync(cancellationToken)) ?? Guid.Empty;
        var participantUsers = await database.DemandParticipants.AsNoTracking()
            .Where(participant => participant.CompanyId == demand.CompanyId && participant.DemandId == demand.Id && participant.DeletedAt == null)
            .Select(participant => participant.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var all = new List<Guid>();
        if (responsibleUser != Guid.Empty)
        {
            all.Add(responsibleUser);
        }
        all.AddRange(participantUsers);
        return new(responsibleUser, [.. all.Distinct()]);
    }

    private async Task SafeNotifyAsync(
        Guid companyId,
        Guid demandId,
        int cycle,
        string kind,
        Audience audience,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Ator real (responsável vigente) sustenta o requisito de ator da
            // função worker-only; sem responsável usa o sintético e o enqueue
            // falha fechado (best-effort Warning, sem afetar a transição).
            var actor = audience.RequesterUserId == Guid.Empty
                ? SystemActor
                : new ActorContext(audience.RequesterUserId, Guid.NewGuid());
            await notifications.ComposeAsync(actor, companyId, demandId, cycle, kind, audience.Users, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Demandas {Operation} {Module} {Result} {Reason} {DemandId}",
                Operation, Module, "succeeded", "notification_flush_deferred", demandId
            );
        }
    }

    /// <summary>GUC scoping VIVE dentro da transação explicitamente aberta pela unidade.</summary>
    private async Task SetCompanyScopeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', '', true), set_config('app.company_id', {companyId.ToString()}, true), set_config('app.correlation_id', {SystemActor.CorrelationId.ToString()}, true)",
            cancellationToken
        );
    }
}

public sealed record DemandasAutomationSummary(int CompaniesScanned, int StagesAdvanced, int DemandsMarkedOverdue);
