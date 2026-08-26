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
/// tick). Cada empresa é processada em transação própria com trava de linha
/// (FOR UPDATE SKIP LOCKED) e eventos append-only idempotentes por chave
/// (demanda, etapa, gatilho) — replay do tick não duplica efeito nem aviso.
/// Corta com decisão documentada: NÃO avança automaticamente para
/// awaiting_approval porque o preparo de aprovação exige ator com capability;
/// a etapa due aguarda avanço manual.
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

    /// <summary>Ator sintético do worker (sem PII; só alimenta GUC/correlação).</summary>
    private static readonly ActorContext SystemActor =
        new(Guid.Empty, Guid.NewGuid());

    public async Task<DemandasAutomationSummary> ScanAsync(CancellationToken cancellationToken)
    {
        var companies = await database.Database
            .SqlQuery<Guid>($"SELECT DISTINCT company_id AS \"Value\" FROM demandas.demands WHERE deleted_at IS NULL AND closed_at IS NULL")
            .ToListAsync(cancellationToken);

        var advanced = 0;
        var markedOverdue = 0;
        foreach (var companyId in companies)
        {
            try
            {
                advanced += await AdvanceDueStagesAsync(companyId, cancellationToken);
                markedOverdue += await MarkOverdueAsync(companyId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Demandas {Operation} {Module} {Result} {Reason} {CompanyId}",
                    Operation,
                    Module,
                    "unavailable",
                    "company_batch_failed",
                    companyId
                );
            }
        }

        if (advanced > 0 || markedOverdue > 0)
        {
            logger.LogInformation(
                "Demandas {Operation} {Module} {Result} {Advanced} {Overdue}",
                Operation,
                Module,
                "succeeded",
                advanced,
                markedOverdue
            );
        }

        return new DemandasAutomationSummary(companies.Count, advanced, markedOverdue);
    }

    private async Task<int> AdvanceDueStagesAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await SetCompanyScopeAsync(companyId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
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

        var advanced = 0;
        List<(Guid DemandId, int Cycle, IReadOnlyList<Guid> Recipients)> flushes = [];
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
                    Operation,
                    Module,
                    "skipped",
                    "awaiting_approval_needs_actor",
                    demand.Id
                );
                continue;
            }

            AppendSystemEvent(
                demand,
                "stage_advanced",
                $"time:{demand.StageCycleCount}:{fromStage}:{toStage}:{demand.StageStartedAt?.UtcTicks ?? now.UtcTicks}",
                now,
                fromStage,
                toStage
            );
            demand.Stage = toStage;
            demand.StageStartedAt = now;
            demand.StageDueAt = ComputeDeadlineFor(stageRules, toStage, now);
            demand.StageCycleCount = demand.StageCycleCount;
            if (toStage == DemandStages.Closed)
            {
                demand.ClosedAt = now;
            }
            demand.OverdueSince = null;

            var entry = database.Entry(demand).Property(row => row.RowVersion);
            entry.CurrentValue = entry.OriginalValue + 1;
            entry.IsModified = true;

            advanced++;
            flushes.Add((demand.Id, demand.StageCycleCount, await RecipientsAsync(demand, cancellationToken)));
        }

        if (advanced > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        foreach (var flush in flushes)
        {
            await SafeNotifyAsync(companyId, flush.DemandId, flush.Cycle, DemandNotificationKinds.StageAdvanced, flush.Recipients, cancellationToken);
        }
        return advanced;
    }

    private async Task<int> MarkOverdueAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await SetCompanyScopeAsync(companyId, cancellationToken);
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

        List<(Guid DemandId, int Cycle, IReadOnlyList<Guid> Recipients)> flushes = [];
        foreach (var demand in candidates)
        {
            // Entre "prazo efetivo vencido" e "agora" pode ter havido avanço no
            // primeiro passo do mesmo tick: só marca se ainda houver prazo vivo.
            var effective = EffectiveDeadlineOf(demand);
            if (!effective.HasValue || effective.Value >= now)
            {
                continue;
            }

            demand.OverdueSince = now;
            AppendSystemEvent(demand, "overdue", $"overdue:{demand.StageCycleCount}:{effective!.Value.UtcTicks}", now);
            var entry = database.Entry(demand).Property(row => row.RowVersion);
            entry.CurrentValue = entry.OriginalValue + 1;
            entry.IsModified = true;
            flushes.Add((demand.Id, demand.StageCycleCount, await RecipientsAsync(demand, cancellationToken)));
        }

        if (flushes.Count > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        foreach (var flush in flushes)
        {
            await SafeNotifyAsync(companyId, flush.DemandId, flush.Cycle, DemandNotificationKinds.Overdue, flush.Recipients, cancellationToken);
        }
        return flushes.Count;
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

    private async Task<List<Guid>> RecipientsAsync(Demand demand, CancellationToken cancellationToken)
    {
        var responsibleUser = await database.Memberships.AsNoTracking()
            .Where(candidate => candidate.CompanyId == demand.CompanyId && candidate.Id == demand.ResponsibleMembershipId)
            .Select(candidate => candidate.UserId)
            .SingleOrDefaultAsync(cancellationToken);
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
        return [.. all.Distinct()];
    }

    private async Task SafeNotifyAsync(
        Guid companyId,
        Guid demandId,
        int cycle,
        string kind,
        IReadOnlyList<Guid> recipients,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await notifications.ComposeAsync(SystemActor, companyId, demandId, cycle, kind, recipients, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Demandas {Operation} {Module} {Result} {Reason} {DemandId}",
                Operation,
                Module,
                "succeeded",
                "notification_flush_deferred",
                demandId
            );
        }
    }

    private async Task SetCompanyScopeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        // Escopo fail-closed apenas com company; user do sistema é vazio.
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', '', true), set_config('app.company_id', {companyId.ToString()}, true), set_config('app.correlation_id', {SystemActor.CorrelationId.ToString()}, true)",
            cancellationToken
        );
    }
}

public sealed record DemandasAutomationSummary(int CompaniesScanned, int StagesAdvanced, int DemandsMarkedOverdue);
