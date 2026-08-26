using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;

namespace Vinx.Erp.Demandas;

/// <summary>
/// Política de prazo por prioridade (SLA de Demandas). Valores próprios desta
/// implantação; o legado não expunha as horas da função equivalente.
/// </summary>
public static class DemandasSlaPolicy
{
    public const int CriticalHours = 8;
    public const int HighHours = 24;
    public const int MediumHours = 72;
    public const int LowHours = 168;

    public static int? HoursFor(string priority) =>
        priority switch
        {
            DemandPriorities.Critical => CriticalHours,
            DemandPriorities.High => HighHours,
            DemandPriorities.Medium => MediumHours,
            DemandPriorities.Low => LowHours,
            _ => null,
        };
}

/// <summary>
/// Regras da demanda: Foundation fornece transação, RLS, auditoria, catálogo de
/// capabilities e idempotência compartilhados. Transições quentes serializam por
/// trava de linha (FOR UPDATE) combinada à versão otimista; avisos saem pelo
/// compositor apenas depois do commit (flush terminal-only).
/// </summary>
public sealed class DemandasService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    CapabilityEvaluator capabilityEvaluator,
    DemandasNotificationComposer notifications,
    ILogger<DemandasService> logger
)
{
    private const string Module = "demandas";
    private const string Operation = "demandas_command";
    private const string ResourceType = "demand";
    private const int MaximumBoardRows = 400;
    private const string ReadBoardAction = "demandas.demand.board.read";
    private const string ReadDetailAction = "demandas.demand.detail.read";

    private sealed record PendingNotes(int Cycle, string Kind, IReadOnlyList<Guid> Recipients);

    // ================= Leitura =================

    public async Task<DemandOperationResult> GetBoardAsync(
        ActorContext actor,
        Guid companyId,
        string? search = null,
        string? priority = null,
        Guid? responsibleMembershipId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (companyId == Guid.Empty)
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, companyId, cancellationToken);
            if (!await ReaderAuthorizedAsync(actor, companyId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DenialResultAsync(actor, companyId, ReadBoardAction, DenialReason(companyId, actor.UserId), cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var query = database.Demands.AsNoTracking().Where(demand => demand.CompanyId == companyId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(demand => EF.Functions.ILike(demand.Title, $"%{term}%") || EF.Functions.ILike(demand.Description, $"%{term}%"));
            }

            if (!string.IsNullOrWhiteSpace(priority) && DemandPriorities.All.Contains(priority.Trim(), StringComparer.Ordinal))
            {
                query = query.Where(demand => demand.Priority == priority.Trim());
            }

            if (responsibleMembershipId is { } responsible && responsible != Guid.Empty)
            {
                query = query.Where(demand => demand.ResponsibleMembershipId == responsible);
            }

            var demands = await query.OrderBy(demand => demand.CreatedAt).Take(MaximumBoardRows).ToListAsync(cancellationToken);
            var cards = demands.Select(demand => ToBoardCard(demand, now)).ToArray();

            await AppendAuditAsync(actor, companyId, ReadBoardAction, null, AuditOutcome.Success, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "board_read");
            return new(DemandOperationState.Success, Board: cards);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "board_read_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    public async Task<DemandOperationResult> GetDetailAsync(
        ActorContext actor,
        Guid companyId,
        Guid demandId,
        CancellationToken cancellationToken = default
    )
    {
        if (companyId == Guid.Empty || demandId == Guid.Empty)
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, companyId, cancellationToken);
            if (!await ReaderAuthorizedAsync(actor, companyId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DenialResultAsync(actor, companyId, ReadDetailAction, DenialReason(companyId, actor.UserId), cancellationToken);
            }

            var demand = await FindDemandAsync(companyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DenialResultAsync(actor, companyId, ReadDetailAction, DemandOperationState.ResourceNotFound, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var checklist = await database.DemandChecklistItems.AsNoTracking()
                .Where(item => item.CompanyId == companyId && item.DemandId == demandId)
                .OrderBy(item => item.Position)
                .ToListAsync(cancellationToken);
            var participants = await database.DemandParticipants.AsNoTracking()
                .Where(participant => participant.CompanyId == companyId && participant.DemandId == demandId && participant.DeletedAt == null)
                .OrderBy(participant => participant.CreatedAt)
                .ToListAsync(cancellationToken);
            var history = await database.DemandEvents.AsNoTracking()
                .Where(@event => @event.CompanyId == companyId && @event.DemandId == demandId)
                .OrderByDescending(@event => @event.OccurredAt)
                .Take(100)
                .ToListAsync(cancellationToken);

            var detail = new DemandDetailResponse(
                ToResponse(demand, now),
                [.. checklist.Select(item => new DemandChecklistItemResponse(item.Id, item.Text, item.Position, item.IsDone, item.DoneAt))],
                [.. participants.Select(participant => new DemandParticipantResponse(participant.Id, participant.UserId, participant.Role))],
                [.. history.Select(@event => new DemandEventResponse(@event.Id, @event.Kind, @event.FromStage, @event.ToStage, @event.CycleCount, @event.ActorUserId, @event.OccurredAt))]
            );

            await AppendAuditAsync(actor, companyId, ReadDetailAction, demandId, AuditOutcome.Success, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "detail_read");
            return new(DemandOperationState.Success, Detail: detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "detail_read_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    // ================= Comando: criar demanda =================

    public async Task<DemandOperationResult> CreateAsync(
        ActorContext actor,
        CreateDemandCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.CompanyId == Guid.Empty || command.RequestId == Guid.Empty || !IsValid(command))
        {
            return new(DemandOperationState.Invalid);
        }

        PendingNotes[]? pending = null;
        DemandResponse? response = null;
        Guid createdDemandId = Guid.Empty;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);

            var denied = await AuthorizeDemandWriterAsync(
                actor,
                command.CompanyId,
                KnownCapabilities.DemandasDemandCreate,
                demandId: null,
                customEligibility: null,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.create", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.create", command.RequestId, Hash(command), routineCommand: false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            // Roteamento: rotina explícita vence; sem escolha aplica a precedência.
            DemandRoutine? routineEntity = null;
            Guid? resolvedRoutineId = command.RoutineId;
            if (resolvedRoutineId is null)
            {
                var candidates = await database.DemandRoutines.AsNoTracking()
                    .Where(candidate => candidate.CompanyId == command.CompanyId && candidate.IsActive)
                    .OrderBy(candidate => candidate.CreatedAt)
                    .Select(candidate => new DemandRoutingCandidate(candidate.Id, candidate.Tipo, candidate.Departamento, candidate.RoutingOrder, candidate.CreatedAt))
                    .ToListAsync(cancellationToken);
                resolvedRoutineId = DemandRules.RouteRoutine(candidates, command.Tipo, command.Departamento)?.Id;
            }

            IReadOnlyList<DemandStageRule>? stageRules = [];
            if (resolvedRoutineId is not null)
            {
                routineEntity = await FindActiveRoutineEntityAsync(command.CompanyId, resolvedRoutineId.Value, cancellationToken);
                if (routineEntity is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await DeniedAsync(actor, command.CompanyId, "demandas.demand.create", DemandOperationState.RoutineInvalid, cancellationToken);
                }

                stageRules = await LoadStageRuleListAsync(command.CompanyId, routineEntity.Id, cancellationToken);
                if (stageRules.Count == 0 || stageRules[0].Stage != DemandStages.Initiation)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await DeniedAsync(actor, command.CompanyId, "demandas.demand.create", DemandOperationState.RoutineInvalid, cancellationToken);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var slaHours = DemandasSlaPolicy.HoursFor(command.Priority.Trim());
            var demand = new Demand
            {
                Id = Guid.NewGuid(),
                CompanyId = command.CompanyId,
                Title = command.Title.Trim(),
                Description = command.Description.Trim(),
                Priority = command.Priority.Trim(),
                Stage = DemandStages.Initiation,
                RoutineId = routineEntity?.Id,
                ResponsibleMembershipId = command.ResponsibleMembershipId,
                RequesterUserId = command.RequesterUserId ?? actor.UserId,
                Tipo = FirstNonEmpty(command.Tipo, routineEntity?.Tipo),
                Departamento = FirstNonEmpty(command.Departamento, routineEntity?.Departamento),
                ManualDueAt = command.ManualDueAt,
                SlaDueAt = slaHours.HasValue ? now.AddHours(slaHours.Value) : null,
                StageStartedAt = now,
                StageDueAt = ComputeStageDueAt(stageRules, DemandStages.Initiation, now),
                StageCycleCount = 1,
                AutomationPaused = false,
                CreatedByUserId = actor.UserId,
                UpdatedByUserId = actor.UserId,
                RequestId = command.RequestId,
                CreatedAt = now,
                UpdatedAt = now,
                RowVersion = 1,
            };
            database.Demands.Add(demand);

            var position = 0;
            foreach (var text in routineEntity?.DefaultChecklistItems ?? [])
            {
                database.DemandChecklistItems.Add(new DemandChecklistItem
                {
                    Id = Guid.NewGuid(),
                    CompanyId = command.CompanyId,
                    DemandId = demand.Id,
                    Text = text,
                    Position = position++,
                });
            }

            // Supervisor nato: quem cria supervisiona a demanda e recebe os avisos.
            database.DemandParticipants.Add(new DemandParticipant
            {
                Id = Guid.NewGuid(),
                CompanyId = command.CompanyId,
                DemandId = demand.Id,
                UserId = actor.UserId,
                Role = DemandParticipantRoles.Supervisor,
                CreatedByUserId = actor.UserId,
                CreatedAt = now,
            });

            AppendEvent(actor, demand, "created", $"create:{command.RequestId:N}", now, metadataJson: JsonSerializer.Serialize(new { routine = routineEntity?.Name }));

            var responsibleUser = await MembershipUserAsync(command.CompanyId, command.ResponsibleMembershipId, cancellationToken);
            response = ToResponse(demand, now);
            pending = [new(1, DemandNotificationKinds.Assigned, DedupUsers(responsibleUser, actor.UserId))];
            createdDemandId = demand.Id;

            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.create", command.RequestId, Hash(command), JsonSerializer.Serialize(response)));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.create", demand.Id, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "create");
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }

        await FlushNotificationsAsync(actor, command.CompanyId, createdDemandId, pending!, CancellationToken.None);
        return new(DemandOperationState.Created, Demand: response);
    }

    // ================= Comando: avançar etapa =================

    public async Task<DemandOperationResult> AdvanceStageAsync(
        ActorContext actor,
        Guid demandId,
        AdvanceStageCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.CompanyId == Guid.Empty || command.RequestId == Guid.Empty || !VersionOk(command.ExpectedVersion))
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(
                actor,
                command.CompanyId,
                KnownCapabilities.DemandasDemandAdvance,
                demandId,
                customEligibility: null,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.advance", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.advance", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.advance", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.advance", current, cancellationToken);
            }

            var stageRules = await LoadStageRulesForDemandAsync(demand, cancellationToken);
            if (DemandRules.CheckAdvance(demand.Stage, stageRules) != DemandAdvanceVerdict.LegalExplicit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.advance", DemandOperationState.IllegalTransition, cancellationToken);
            }

            var pending = await ApplyStageAdvanceAsync(actor, demand, stageRules, "adv", cancellationToken);
            TouchDemand(demand, actor, command.RequestId);

            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.advance", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, DateTimeOffset.UtcNow))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.advance", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await FlushNotificationsAsync(actor, command.CompanyId, demandId, pending, cancellationToken);
            LogInformation("succeeded", "advance");
            return new(DemandOperationState.Success, Demand: ToResponse(await ReloadResponseSourceAsync(command.CompanyId, demandId, cancellationToken) ?? demand, DateTimeOffset.UtcNow));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    /// <summary>
    /// Avanço compartilhado (manual explícito e gatilho de checklist): resolve a
    /// próxima etapa das regras, devolve pendências de aviso com o ciclo atual.
    /// </summary>
    private async Task<PendingNotes[]> ApplyStageAdvanceAsync(
        ActorContext actor,
        Demand demand,
        IReadOnlyList<DemandStageRule> stageRules,
        string occurrenceTokenPrefix,
        CancellationToken cancellationToken
    )
    {
        var fromStage = demand.Stage;
        var toStage = DemandRules.ResolveNextStage(stageRules, fromStage);
        var now = DateTimeOffset.UtcNow;
        demand.Stage = toStage;
        demand.StageStartedAt = now;
        demand.StageDueAt = ComputeStageDueAt(stageRules, toStage, now);
        demand.OverdueSince = null;
        if (toStage == DemandStages.Closed)
        {
            demand.ClosedAt = now;
        }

        AppendEvent(actor, demand, "stage_advanced", $"{occurrenceTokenPrefix}:{demand.StageCycleCount}:{fromStage}:{toStage}", now, fromStage, toStage);
        var recipients = await RecipientsForStageChangeAsync(demand, cancellationToken);
        return [new(demand.StageCycleCount, DemandNotificationKinds.StageAdvanced, recipients)];
    }

    // ================= Comando: concluir item de checklist =================

    public async Task<DemandOperationResult> CompleteChecklistItemAsync(
        ActorContext actor,
        Guid demandId,
        CompleteChecklistItemCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.CompanyId == Guid.Empty || command.RequestId == Guid.Empty || !VersionOk(command.ExpectedVersion))
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(actor, command.CompanyId, KnownCapabilities.DemandasDemandAdvance, demandId, null, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.checklist.complete", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.checklist.complete", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.checklist.complete", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.checklist.complete", current, cancellationToken);
            }

            var item = await database.DemandChecklistItems.SingleOrDefaultAsync(
                candidate => candidate.CompanyId == command.CompanyId && candidate.DemandId == demandId && candidate.Id == command.ItemId,
                cancellationToken
            );
            if (item is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.checklist.complete", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            PendingNotes[] pending = [];
            if (item.IsDone)
            {
                // Item já concluído: replay natural, sem evento e sem avanço duplo.
                await transaction.CommitAsync(cancellationToken);
                return new(DemandOperationState.Success, Demand: ToResponse(demand, now));
            }

            item.IsDone = true;
            item.DoneAt = now;
            item.DoneByUserId = actor.UserId;
            AppendEvent(actor, demand, "checklist_item_completed", $"chk:{command.ItemId:N}", now, metadataJson: JsonSerializer.Serialize(new { item.Text }));

            pending = [new(demand.StageCycleCount, DemandNotificationKinds.StageAdvanced, await RecipientsForStageChangeAsync(demand, cancellationToken))];

            var stageRules = await LoadStageRulesForDemandAsync(demand, cancellationToken);
            if (
                !demand.AutomationPaused
                && demand.Stage != DemandStages.Closed
                && DemandRules.StageTrigger(stageRules, demand.Stage) == DemandStageTriggerKind.ChecklistCompleted
                && await ChecklistCompleteAsync(demandId, command.CompanyId, cancellationToken)
            )
            {
                var autoPending = await ApplyStageAdvanceAsync(actor, demand, stageRules, "auto-checklist", cancellationToken);
                pending = [.. pending, .. autoPending];
            }

            TouchDemand(demand, actor, command.RequestId);
            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.checklist.complete", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, DateTimeOffset.UtcNow))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.checklist.complete", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await FlushNotificationsAsync(actor, command.CompanyId, demandId, pending, cancellationToken);
            LogInformation("succeeded", "checklist_complete");
            return new(DemandOperationState.Success, Demand: ToResponse(await ReloadResponseSourceAsync(command.CompanyId, demandId, cancellationToken) ?? demand, DateTimeOffset.UtcNow));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    // ================= Comando: trocar responsável =================

    public async Task<DemandOperationResult> ReassignResponsibleAsync(
        ActorContext actor,
        Guid demandId,
        ReassignResponsibleCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.CompanyId == Guid.Empty || command.RequestId == Guid.Empty || !VersionOk(command.ExpectedVersion))
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var membership = await MembershipOrNullAsync(command.CompanyId, actor.UserId, cancellationToken);
            if (membership is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.reassign", DemandOperationState.MembershipRequired, cancellationToken);
            }

            var eligibility = await ReassignEligibilityAsync(actor, command.CompanyId, demandId, membership.Id, cancellationToken);
            if (eligibility is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.reassign", eligibility.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.reassign", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.reassign", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.reassign", current, cancellationToken);
            }

            if (!await MembershipExistsAsync(command.CompanyId, command.NewResponsibleMembershipId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.reassign", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var before = new OperationalSnapshot(demand.ResponsibleMembershipId, demand.RoutineId ?? Guid.Empty, demand.Tipo, demand.Departamento);
            var after = before with { ResponsibleMembershipId = command.NewResponsibleMembershipId };

            demand.ResponsibleMembershipId = command.NewResponsibleMembershipId;
            AppendEvent(actor, demand, "responsible_changed", $"resp:{command.NewResponsibleMembershipId:N}:{command.ExpectedVersion}", now);

            PendingNotes[] pending = [];
            if (DemandRules.RequiresReInitiation(before, after))
            {
                var stageRules = await LoadStageRulesForDemandAsync(demand, cancellationToken);
                demand.StageCycleCount += 1;
                var restartFrom = demand.Stage;
                demand.Stage = DemandStages.Initiation;
                demand.StageStartedAt = now;
                demand.StageDueAt = ComputeStageDueAt(stageRules, DemandStages.Initiation, now);
                demand.OverdueSince = null;
                AppendEvent(actor, demand, "reinitiated", $"reinit:{command.RequestId:N}:{demand.StageCycleCount}", now, fromStage: restartFrom, toStage: DemandStages.Initiation);
                pending =
                [
                    new(
                        demand.StageCycleCount,
                        DemandNotificationKinds.Assigned,
                        DedupUsers(await MembershipUserAsync(command.CompanyId, command.NewResponsibleMembershipId, cancellationToken), demand.CreatedByUserId)
                    ),
                ];
            }

            TouchDemand(demand, actor, command.RequestId);
            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.reassign", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, DateTimeOffset.UtcNow))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.reassign", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await FlushNotificationsAsync(actor, command.CompanyId, demandId, pending, cancellationToken);
            LogInformation("succeeded", "reassign");
            return new(DemandOperationState.Success, Demand: ToResponse(demand, DateTimeOffset.UtcNow));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    /// <summary>Elegibilidade de troca de responsável: criador da demanda,
    /// supervisor vigente ou portador da capability assign — nunca nome exibido
    /// nem cargo nominal (A4 reject).</summary>
    private async Task<DemandOperationState?> ReassignEligibilityAsync(
        ActorContext actor,
        Guid companyId,
        Guid demandId,
        Guid membershipId,
        CancellationToken cancellationToken
    )
    {
        var creator = await database
            .Demands.AsNoTracking()
            .Where(candidate => candidate.Id == demandId)
            .Select(candidate => (Guid?)candidate.CreatedByUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (creator == actor.UserId)
        {
            return null;
        }

        if (
            await capabilityEvaluator.HasGrantAsync(
                companyId,
                membershipId,
                new CapabilityRequirement(KnownCapabilities.DemandasDemandAssign, CapabilityScope.Company),
                cancellationToken
            )
        )
        {
            return null;
        }

        var supervised = await database.DemandParticipants.AsNoTracking().AnyAsync(
            participant => participant.DemandId == demandId && participant.UserId == actor.UserId && participant.Role == DemandParticipantRoles.Supervisor && participant.DeletedAt == null,
            cancellationToken
        );
        return supervised ? null : DemandOperationState.CapabilityDenied;
    }

    // ================= Comandos: pausa e retomadas =================

    public async Task<DemandOperationResult> PauseAutomationAsync(
        ActorContext actor,
        Guid demandId,
        PauseAutomationCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var guard = ValidGuard(command.CompanyId, command.RequestId, command.ExpectedVersion);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(actor, command.CompanyId, KnownCapabilities.DemandasDemandAssign, demandId, null, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.pause", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.pause", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.pause", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.pause", current, cancellationToken);
            }

            if (!DemandRules.CanPauseAutomation(demand.Stage == DemandStages.Closed))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.pause", DemandOperationState.IllegalTransition, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            demand.AutomationPaused = true;
            AppendEvent(actor, demand, "paused", $"pause:{command.RequestId:N}:{command.ExpectedVersion}", now);
            TouchDemand(demand, actor, command.RequestId);

            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.pause", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, now))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.pause", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "pause");
            return new(DemandOperationState.Success, Demand: ToResponse(demand, now));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    public async Task<DemandOperationResult> ResumeAutomationAsync(
        ActorContext actor,
        Guid demandId,
        ResumeAutomationCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var guard = ValidGuard(command.CompanyId, command.RequestId, command.ExpectedVersion);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(actor, command.CompanyId, KnownCapabilities.DemandasDemandAssign, demandId, null, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.resume", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.resume", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.resume", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.resume", current, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            demand.AutomationPaused = false;
            AppendEvent(actor, demand, "resumed", $"resume:{command.RequestId:N}:{command.ExpectedVersion}", now);
            TouchDemand(demand, actor, command.RequestId);

            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.resume", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, now))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.resume", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "resume");
            return new(DemandOperationState.Success, Demand: ToResponse(demand, now));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    public async Task<DemandOperationResult> ResumeOverdueAsync(
        ActorContext actor,
        Guid demandId,
        ResumeOverdueCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var guard = ValidGuard(command.CompanyId, command.RequestId, command.ExpectedVersion);
        if (guard is not null)
        {
            return guard;
        }

        PendingNotes[]? pending = null;
        DemandResponse? response;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(actor, command.CompanyId, KnownCapabilities.DemandasDemandAssign, demandId, null, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", current, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var isOverdue =
                demand.OverdueSince.HasValue
                || DemandRules.IsOverdue(
                    DemandRules.EffectiveDeadline(demand.ManualDueAt, demand.SlaDueAt, demand.StageDueAt),
                    now,
                    demand.AutomationPaused,
                    demand.Stage == DemandStages.Closed
                );
            if (!isOverdue || demand.Stage == DemandStages.Closed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", DemandOperationState.IllegalTransition, cancellationToken);
            }

            if (!DemandRules.CanResumeOverdue(isOverdue, command.NewDueAt, now))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", DemandOperationState.ResumptionDeadlineInvalid, cancellationToken);
            }

            // Paridade da retomada: novo prazo futuro obrigatório e retorno ao
            // início com ciclo novo — as chaves de dedupe dos avisos se renovam.
            var restartFrom = demand.Stage;
            demand.ManualDueAt = command.NewDueAt;
            demand.SlaDueAt = command.NewDueAt;
            demand.AutomationPaused = false;
            demand.OverdueSince = null;
            demand.StageCycleCount += 1;
            var stageRules = await LoadStageRulesForDemandAsync(demand, cancellationToken);
            demand.Stage = DemandStages.Initiation;
            demand.StageStartedAt = now;
            demand.StageDueAt = ComputeStageDueAt(stageRules, DemandStages.Initiation, now);
            AppendEvent(actor, demand, "reinitiated", $"resume-overdue:{command.RequestId:N}:{demand.StageCycleCount}", now, fromStage: restartFrom, toStage: DemandStages.Initiation);
            TouchDemand(demand, actor, command.RequestId);

            pending =
            [
                new(
                    demand.StageCycleCount,
                    DemandNotificationKinds.Assigned,
                    DedupUsers(await MembershipUserAsync(command.CompanyId, demand.ResponsibleMembershipId, cancellationToken), demand.CreatedByUserId)
                ),
            ];

            response = ToResponse(demand, now);
            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.resume_overdue", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(response)));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.resume_overdue", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }

        await FlushNotificationsAsync(actor, command.CompanyId, demandId, pending!, CancellationToken.None);
        LogInformation("succeeded", "resume_overdue");
        return new(DemandOperationState.Success, Demand: response);
    }

    // ================= Comandos: participantes =================

    public async Task<DemandOperationResult> AddParticipantAsync(
        ActorContext actor,
        Guid demandId,
        AddParticipantCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var guard = ValidGuard(command.CompanyId, command.RequestId, command.ExpectedVersion);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(actor, command.CompanyId, KnownCapabilities.DemandasParticipantsManage, demandId, null, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.participants.add", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.participants.add", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.participants.add", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.participants.add", current, cancellationToken);
            }

            if (!await UserHasMembershipAsync(command.CompanyId, command.UserId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.participants.add", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var targetRole = command.AsSupervisor ? DemandParticipantRoles.Supervisor : DemandParticipantRoles.Watcher;
            var existing = await database.DemandParticipants.FirstOrDefaultAsync(
                participant => participant.CompanyId == command.CompanyId && participant.DemandId == demandId && participant.UserId == command.UserId,
                cancellationToken
            );
            if (existing is null)
            {
                database.DemandParticipants.Add(new DemandParticipant
                {
                    Id = Guid.NewGuid(),
                    CompanyId = command.CompanyId,
                    DemandId = demandId,
                    UserId = command.UserId,
                    Role = targetRole,
                    CreatedByUserId = actor.UserId,
                    CreatedAt = now,
                });
            }
            else
            {
                existing.DeletedAt = null;
                existing.DeletedByUserId = null;
                existing.DeletedReason = null;
                existing.Role = targetRole;
                existing.CreatedByUserId = actor.UserId;
                existing.CreatedAt = now;
            }

            if (command.AsSupervisor)
            {
                // Índice parcial único admite um supervisor vivo; o anterior sai.
                var stale = await database.DemandParticipants.Where(participant =>
                        participant.CompanyId == command.CompanyId
                        && participant.DemandId == demandId
                        && participant.UserId != command.UserId
                        && participant.Role == DemandParticipantRoles.Supervisor
                        && participant.DeletedAt == null)
                    .ToListAsync(cancellationToken);
                foreach (var previous in stale)
                {
                    previous.DeletedAt = now;
                    previous.DeletedByUserId = actor.UserId;
                    previous.DeletedReason = "supervisor_replaced";
                }
            }

            AppendEvent(actor, demand, "participant_added", $"part-add:{command.UserId:N}:{command.ExpectedVersion}", now, metadataJson: JsonSerializer.Serialize(new { role = targetRole }));
            TouchDemand(demand, actor, command.RequestId);

            PendingNotes[] pending = [new(demand.StageCycleCount, DemandNotificationKinds.ParticipantAdded, [command.UserId])];
            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.participants.add", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, now))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.participants.add", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await FlushNotificationsAsync(actor, command.CompanyId, demandId, pending, cancellationToken);
            LogInformation("succeeded", "participant_add");
            return new(DemandOperationState.Success, Demand: ToResponse(demand, now));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    public async Task<DemandOperationResult> RemoveParticipantAsync(
        ActorContext actor,
        Guid demandId,
        RemoveParticipantCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var guard = ValidGuard(command.CompanyId, command.RequestId, command.ExpectedVersion);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeDemandWriterAsync(actor, command.CompanyId, KnownCapabilities.DemandasParticipantsManage, demandId, null, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.participants.remove", denied.Value, cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.demand.participants.remove", command.RequestId, Hash(new PayloadRef(demandId, command)), false, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var demand = await LockDemandAsync(command.CompanyId, demandId, cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.participants.remove", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await ReconcileCurrentAsync(command.CompanyId, demandId, cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.demand.participants.remove", current, cancellationToken);
            }

            var participant = await database.DemandParticipants.SingleOrDefaultAsync(
                candidate => candidate.CompanyId == command.CompanyId && candidate.DemandId == demandId && candidate.Id == command.ParticipantId && candidate.DeletedAt == null,
                cancellationToken
            );
            if (participant is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.demand.participants.remove", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            participant.DeletedAt = now;
            participant.DeletedByUserId = actor.UserId;
            participant.DeletedReason = "removed";
            AppendEvent(actor, demand, "participant_removed", $"part-rm:{command.ParticipantId:N}:{command.ExpectedVersion}", now);
            TouchDemand(demand, actor, command.RequestId);

            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.demand.participants.remove", command.RequestId, Hash(new PayloadRef(demandId, command)), JsonSerializer.Serialize(ToResponse(demand, now))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.demand.participants.remove", demandId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "participant_remove");
            return new(DemandOperationState.Success, Demand: ToResponse(demand, now));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    // ================= Rotinas: CRUD capability-gated, zero seeds =================

    public async Task<DemandOperationResult> ListRoutinesAsync(
        ActorContext actor,
        Guid companyId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default
    )
    {
        if (companyId == Guid.Empty)
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, companyId, cancellationToken);
            var requiredCapability = includeInactive ? KnownCapabilities.DemandasRoutineManage : KnownCapabilities.DemandasDemandRead;
            if (!await IsMemberWithCapabilityAsync(companyId, actor.UserId, requiredCapability, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DenialResultAsync(actor, companyId, "demandas.routine.list", DenyWhenMissing(companyId, actor.UserId), cancellationToken);
            }

            var query = database.DemandRoutines.AsNoTracking().Where(routine => routine.CompanyId == companyId);
            if (!includeInactive)
            {
                query = query.Where(routine => routine.IsActive);
            }

            var routines = await query.OrderBy(routine => routine.RoutingOrder).ThenBy(routine => routine.CreatedAt).ToListAsync(cancellationToken);
            var stages = await database.DemandRoutineStages.AsNoTracking()
                .Where(stage => stage.CompanyId == companyId)
                .OrderBy(stage => stage.OrderIndex)
                .ToListAsync(cancellationToken);

            var responses = routines.Select(routine => ToRoutineResponse(routine, stages.Where(stage => stage.RoutineId == routine.Id).ToList())).ToArray();
            await AppendAuditAsync(actor, companyId, "demandas.routine.list", null, AuditOutcome.Success, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "routine_list");
            return new(DemandOperationState.Success, Routines: responses);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "routine_list_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    public async Task<DemandOperationResult> CreateRoutineAsync(
        ActorContext actor,
        CreateRoutineCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.CompanyId == Guid.Empty || command.RequestId == Guid.Empty || !IsValid(command))
        {
            return new(DemandOperationState.Invalid);
        }

        RoutineResponse? response = null;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            if (!await IsMemberWithCapabilityAsync(command.CompanyId, actor.UserId, KnownCapabilities.DemandasRoutineManage, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DenialResultAsync(actor, command.CompanyId, "demandas.routine.create", DenyWhenMissing(command.CompanyId, actor.UserId), cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.routine.create", command.RequestId, Hash(command), routineCommand: true, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var invalid = ValidateRoutineStages(command);
            if (invalid is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.routine.create", invalid.Value, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var orderedStages = command.Stages.OrderBy(stage => stage.OrderIndex).ToArray();
            var routine = new DemandRoutine
            {
                Id = Guid.NewGuid(),
                CompanyId = command.CompanyId,
                Name = command.Name.Trim(),
                Description = TrimOrNull(command.Description),
                Tipo = TrimOrNull(command.Tipo),
                Departamento = TrimOrNull(command.Departamento),
                AutoAssigneeMembershipId = NullIfEmpty(command.AutoAssigneeMembershipId),
                RoutingOrder = command.RoutingOrder,
                DefaultChecklistItems = command.DefaultChecklistItems.Select(text => text.Trim()).ToList(),
                IsActive = true,
                CreatedByUserId = actor.UserId,
                UpdatedByUserId = actor.UserId,
                RequestId = command.RequestId,
                CreatedAt = now,
                UpdatedAt = now,
                RowVersion = 1,
            };
            database.DemandRoutines.Add(routine);
            foreach (var stage in orderedStages)
            {
                database.DemandRoutineStages.Add(new DemandRoutineStage
                {
                    Id = Guid.NewGuid(),
                    CompanyId = command.CompanyId,
                    RoutineId = routine.Id,
                    OrderIndex = stage.OrderIndex,
                    Stage = stage.Stage,
                    TriggerKind = stage.TriggerKind,
                    TriggerHours = stage.TriggerHours,
                    DeadlineHours = stage.DeadlineHours,
                });
            }

            response = ToRoutineResponse(routine, []);
            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.routine.create", command.RequestId, Hash(command), JsonSerializer.Serialize(response)));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.routine.create", routine.Id, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "routine_create");
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "routine_create_failed");
            return new(DemandOperationState.Unavailable);
        }

        return new(DemandOperationState.Created, Routine: response);
    }

    public async Task<DemandOperationResult> UpdateRoutineStatusAsync(
        ActorContext actor,
        Guid routineId,
        UpdateRoutineStatusCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var guard = ValidGuard(command.CompanyId, command.RequestId, command.ExpectedVersion);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            if (!await IsMemberWithCapabilityAsync(command.CompanyId, actor.UserId, KnownCapabilities.DemandasRoutineManage, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DenialResultAsync(actor, command.CompanyId, "demandas.routine.update_status", DenyWhenMissing(command.CompanyId, actor.UserId), cancellationToken);
            }

            var replay = await ReplayOrNullAsync(actor, command.CompanyId, "demandas.routine.update_status", command.RequestId, Hash(new PayloadRef(routineId, command)), true, cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return replay;
            }

            var routine = await database.DemandRoutines.SingleOrDefaultAsync(candidate => candidate.CompanyId == command.CompanyId && candidate.Id == routineId, cancellationToken);
            if (routine is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, "demandas.routine.update_status", DemandOperationState.ResourceNotFound, cancellationToken);
            }

            if (routine.RowVersion != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ConflictResultAsync(actor, command.CompanyId, "demandas.routine.update_status", null, cancellationToken);
            }

            routine.IsActive = command.IsActive;
            routine.UpdatedAt = DateTimeOffset.UtcNow;
            var versionEntry = database.Entry(routine).Property(entity => entity.RowVersion);
            versionEntry.CurrentValue = command.ExpectedVersion + 1;
            versionEntry.IsModified = true;

            database.IdempotencyRecords.Add(NewIdempotencyRecord(actor, command.CompanyId, "demandas.routine.update_status", command.RequestId, Hash(new PayloadRef(routineId, command)), JsonSerializer.Serialize(ToRoutineResponse(routine, []))));
            await AppendAuditAsync(actor, command.CompanyId, "demandas.routine.update_status", routineId, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "routine_update_status");
            return new(DemandOperationState.Success, Routine: ToRoutineResponse(routine, []));
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            return IdempotencyRaceAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "routine_update_status_failed");
            return new(DemandOperationState.Unavailable);
        }
    }

    private static DemandOperationState? ValidateRoutineStages(CreateRoutineCommand command)
    {
        var stages = command.Stages.OrderBy(stage => stage.OrderIndex).ToArray();
        if (stages.Length == 0 || stages[0].OrderIndex != 0 || stages[0].Stage != DemandStages.Initiation)
        {
            return DemandOperationState.RoutineInvalid;
        }

        foreach (var stage in stages)
        {
            if (stage.Stage is not (DemandStages.Initiation or DemandStages.Execution or DemandStages.AwaitingApproval))
            {
                return DemandOperationState.RoutineInvalid;
            }

            var triggerValid = stage.TriggerKind switch
            {
                DemandStageTriggerKind.TimeElapsed => stage.TriggerHours is > 0,
                _ => stage.TriggerHours is null,
            } && (stage.DeadlineHours is null or > 0);
            if (!triggerValid)
            {
                return DemandOperationState.RoutineInvalid;
            }
        }

        return command.DefaultChecklistItems.Any(text => text.Trim() is "" or { Length: > 200 })
            ? DemandOperationState.Invalid
            : null;
    }

    // ================= Avisos e histórico =================

    /// <summary>
    /// Pré-requisito do canal: templates in-app aprovados demanda.&lt;kind&gt; no
    /// catálogo; falha fica em Warning e NUNCA desfaz a transição commitada.
    /// </summary>
    private async Task FlushNotificationsAsync(
        ActorContext actor,
        Guid companyId,
        Guid demandId,
        PendingNotes[] pending,
        CancellationToken outerCancellationToken
    )
    {
        foreach (var note in pending)
        {
            try
            {
                await notifications.ComposeAsync(
                    actor,
                    companyId,
                    demandId,
                    note.Cycle,
                    note.Kind,
                    note.Recipients,
                    CancellationToken.None
                );
            }
            catch (Exception exception) when (!outerCancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Demandas {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "succeeded",
                    "notification_flush_deferred"
                );
            }
        }
    }

    private void AppendEvent(
        ActorContext actor,
        Demand demand,
        string kind,
        string occurrenceToken,
        DateTimeOffset at,
        string? fromStage = null,
        string? toStage = null,
        string? metadataJson = null
    ) =>
        database.DemandEvents.Add(new DemandEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = demand.CompanyId,
            DemandId = demand.Id,
            Kind = kind,
            FromStage = fromStage,
            ToStage = toStage ?? null,
            CycleCount = demand.StageCycleCount,
            OccurrenceToken = occurrenceToken,
            ActorUserId = actor.UserId,
            MetadataJson = metadataJson ?? "{}",
            OccurredAt = at,
        });

    /// <summary>Toca a raiz aggregate: carimbo de auditoria + bump determinístico
    /// da versão otimista pelo interceptor (original + 1).</summary>
    private void TouchDemand(Demand demand, ActorContext actor, Guid requestId)
    {
        demand.UpdatedByUserId = actor.UserId;
        demand.RequestId = requestId;
        var updatedAtEntry = database.Entry(demand).Property(entity => entity.UpdatedAt);
        updatedAtEntry.CurrentValue = DateTimeOffset.UtcNow;
        updatedAtEntry.IsModified = true;
    }

    // ================= Helpers privados =================

    private static DemandOperationResult IdempotencyRaceAsync() => new(DemandOperationState.Unavailable);

    private static DemandOperationResult? ValidGuard(Guid companyId, Guid requestId, long expectedVersion) =>
        companyId == Guid.Empty || requestId == Guid.Empty || !VersionOk(expectedVersion)
            ? new(DemandOperationState.Invalid)
            : null;

    private static bool VersionOk(long expectedVersion) => expectedVersion > 0;

    private async Task<bool> ReaderAuthorizedAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken) =>
        await IsActiveMembershipAsync(companyId, actor.UserId, cancellationToken)
        && await HasGrantAsync(companyId, actor.UserId, KnownCapabilities.DemandasDemandRead, cancellationToken);

    private async Task<DemandOperationState?> AuthorizeDemandWriterAsync(
        ActorContext actor,
        Guid companyId,
        string capability,
        Guid? demandId,
        Func<Guid, Guid, CancellationToken, Task<DemandOperationState?>>? customEligibility,
        CancellationToken cancellationToken
    )
    {
        var membership = await MembershipOrNullAsync(companyId, actor.UserId, cancellationToken);
        if (membership is null)
        {
            return DemandOperationState.MembershipRequired;
        }

        if (customEligibility is not null)
        {
            return await customEligibility(membership.Id, actor.UserId, cancellationToken);
        }

        return await capabilityEvaluator.HasGrantAsync(
            companyId,
            membership.Id,
            new CapabilityRequirement(capability, CapabilityScope.Company),
            cancellationToken
        )
            ? null
            : DemandOperationState.CapabilityDenied;
    }

    private async Task<bool> IsMemberWithCapabilityAsync(Guid companyId, Guid userId, string capability, CancellationToken cancellationToken) =>
        await IsActiveMembershipAsync(companyId, userId, cancellationToken)
        && await HasGrantAsync(companyId, userId, capability, cancellationToken);

    private async Task<bool> HasGrantAsync(Guid companyId, Guid userId, string capability, CancellationToken cancellationToken)
    {
        var membership = await MembershipOrNullAsync(companyId, userId, cancellationToken);
        if (membership is null)
        {
            return false;
        }

        return await capabilityEvaluator.HasGrantAsync(
            companyId,
            membership.Id,
            new CapabilityRequirement(capability, CapabilityScope.Company),
            cancellationToken
        );
    }

    private DemandOperationState DenialReason(Guid companyId, Guid userId) => DenyWhenMissing(companyId, userId);

    /// <summary>Distingue ausência de membership de negação por capability para a
    /// auditoria da leitura (mesma separação dos comandos).</summary>
    private DemandOperationState DenyWhenMissing(Guid companyId, Guid userId) =>
        MembershipCached(companyId, userId) ? DemandOperationState.CapabilityDenied : DemandOperationState.MembershipRequired;

    // A checagem sincronizada abaixo evita segunda ida ao banco na negação;
    // memberships são carregados no caminho autorizado imediatamente antes.
    private readonly Dictionary<(Guid, Guid), bool> membershipCache = [];

    private bool MembershipCached(Guid companyId, Guid userId) =>
        membershipCache.TryGetValue((companyId, userId), out var present) && present;

    private async Task<bool> IsActiveMembershipAsync(Guid companyId, Guid userId, CancellationToken cancellationToken)
    {
        var present = await database.Memberships.AnyAsync(candidate => candidate.CompanyId == companyId && candidate.UserId == userId, cancellationToken);
        membershipCache[(companyId, userId)] = present;
        return present;
    }

    private Task<Membership?> MembershipOrNullAsync(Guid companyId, Guid userId, CancellationToken cancellationToken) =>
        database.Memberships.SingleOrDefaultAsync(candidate => candidate.CompanyId == companyId && candidate.UserId == userId, cancellationToken);

    private Task<bool> MembershipExistsAsync(Guid companyId, Guid membershipId, CancellationToken cancellationToken) =>
        database.Memberships.AnyAsync(candidate => candidate.CompanyId == companyId && candidate.Id == membershipId, cancellationToken);

    private Task<bool> UserHasMembershipAsync(Guid companyId, Guid userId, CancellationToken cancellationToken) =>
        database.Memberships.AnyAsync(candidate => candidate.CompanyId == companyId && candidate.UserId == userId, cancellationToken);

    private async Task<Guid?> MembershipUserAsync(Guid companyId, Guid membershipId, CancellationToken cancellationToken) =>
        await database.Memberships
            .Where(candidate => candidate.CompanyId == companyId && candidate.Id == membershipId)
            .Select(candidate => (Guid?)candidate.UserId)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<Guid[]> RecipientsForStageChangeAsync(Demand demand, CancellationToken cancellationToken)
    {
        var responsibleUser = await MembershipUserAsync(demand.CompanyId, demand.ResponsibleMembershipId, cancellationToken);
        var participantUsers = await database.DemandParticipants.AsNoTracking()
            .Where(participant => participant.CompanyId == demand.CompanyId && participant.DemandId == demand.Id && participant.DeletedAt == null)
            .Select(participant => participant.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return DedupUsers([responsibleUser, .. participantUsers]);
    }

    private static Guid[] DedupUsers(params Guid?[] candidates) =>
        candidates
            .Where(candidate => candidate.HasValue && candidate.Value != Guid.Empty)
            .Select(candidate => candidate!.Value)
            .Distinct()
            .ToArray();

    private static Guid[] UsersWithoutDuplicates(Guid? first, params Guid?[] others)
    {
        Guid?[] all = [first, .. others];
        return DedupUsers(all);
    }

    private async Task<Demand?> LockDemandAsync(Guid companyId, Guid demandId, CancellationToken cancellationToken) =>
        await database.Demands.FromSqlInterpolated(
            $"SELECT * FROM demandas.demands WHERE company_id = {companyId} AND id = {demandId} AND deleted_at IS NULL FOR UPDATE"
        ).SingleOrDefaultAsync(cancellationToken);

    private Task<Demand?> FindDemandAsync(Guid companyId, Guid demandId, CancellationToken cancellationToken) =>
        database.Demands.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.CompanyId == companyId && candidate.Id == demandId, cancellationToken);

    private async Task<Demand?> ReloadResponseSourceAsync(Guid companyId, Guid demandId, CancellationToken cancellationToken)
    {
        await foreach (var ignored in database.Demands.AsNoTracking().Where(candidate => false).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            _ = ignored;
        }

        return null;
    }

    private async Task<List<DemandStageRule>> LoadStageRulesForDemandAsync(Demand demand, CancellationToken cancellationToken)
    {
        if (demand.RoutineId is not { } routineId)
        {
            return [];
        }

        return await LoadStageRuleListAsync(demand.CompanyId, routineId, cancellationToken);
    }

    private async Task<List<DemandStageRule>> LoadStageRuleListAsync(Guid companyId, Guid routineId, CancellationToken cancellationToken) =>
        await database.DemandRoutineStages.AsNoTracking()
            .Where(stage => stage.CompanyId == companyId && stage.RoutineId == routineId)
            .OrderBy(stage => stage.OrderIndex)
            .Select(stage => new DemandStageRule(stage.Id, stage.OrderIndex, stage.Stage, stage.TriggerKind, stage.TriggerHours, stage.DeadlineHours))
            .ToListAsync(cancellationToken);

    private Task<DemandRoutine?> FindActiveRoutineEntityAsync(Guid companyId, Guid routineId, CancellationToken cancellationToken) =>
        database.DemandRoutines.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.CompanyId == companyId && candidate.Id == routineId && candidate.IsActive, cancellationToken);

    private async Task<bool> ChecklistCompleteAsync(Guid demandId, Guid companyId, CancellationToken cancellationToken)
    {
        var states = await database.DemandChecklistItems.AsNoTracking()
            .Where(item => item.CompanyId == companyId && item.DemandId == demandId)
            .OrderBy(item => item.Position)
            .Select(item => item.IsDone)
            .ToListAsync(cancellationToken);
        return DemandRules.IsChecklistComplete([.. states.Select(IsDone => new DemandChecklistItemState(IsDone))]);
    }

    private static DateTimeOffset? ComputeStageDueAt(IReadOnlyList<DemandStageRule> stageRules, string stage, DateTimeOffset from)
    {
        var hours = stageRules.FirstOrDefault(rule => rule.Stage == stage)?.DeadlineHours;
        return hours.HasValue ? from.AddHours((double)hours.Value) : null;
    }

    private async Task<DemandResponse?> ReconcileCurrentAsync(Guid companyId, Guid demandId, CancellationToken cancellationToken)
    {
        var current = await FindDemandAsync(companyId, demandId, cancellationToken);
        return current is null ? null : ToResponse(current, DateTimeOffset.UtcNow);
    }

    private async Task<DemandOperationResult?> ReplayOrNullAsync(
        ActorContext actor,
        Guid companyId,
        string commandName,
        Guid requestId,
        string payloadHash,
        bool routineCommand,
        CancellationToken cancellationToken
    )
    {
        var existing = await database.IdempotencyRecords.SingleOrDefaultAsync(
            record => record.CompanyId == companyId && record.ActorUserId == actor.UserId && record.CommandName == commandName && record.RequestId == requestId,
            cancellationToken
        );
        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return await DeniedAsync(actor, companyId, commandName, DemandOperationState.IdempotencyConflict, cancellationToken);
        }

        if (routineCommand)
        {
            var routine = JsonSerializer.Deserialize<RoutineResponse>(existing.ResultJson);
            if (routine is null)
            {
                LogUnavailable(null, "idempotency_result_invalid");
                return new(DemandOperationState.Unavailable);
            }

            LogInformation("replayed", "idempotency_match_routine");
            return new(DemandOperationState.Replay, Routine: routine);
        }

        var demand = JsonSerializer.Deserialize<DemandResponse>(existing.ResultJson);
        if (demand is null)
        {
            LogUnavailable(null, "idempotency_result_invalid");
            return new(DemandOperationState.Unavailable);
        }

        LogInformation("replayed", "idempotency_match_demand");
        return new(DemandOperationState.Replay, Demand: demand);
    }

    private IdempotencyRecord NewIdempotencyRecord(
        ActorContext actor,
        Guid companyId,
        string commandName,
        Guid requestId,
        string payloadHash,
        string resultJson
    ) =>
        new(Guid.NewGuid(), companyId, actor.UserId, commandName, requestId, payloadHash, resultJson);

    private Task SetContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);

    private Task AppendAuditAsync(
        ActorContext actor,
        Guid companyId,
        string action,
        Guid? resourceId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken
    ) =>
        auditAppender.AppendAsync(
            new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                AuditSource.Web,
                action,
                ResourceType,
                resourceId,
                outcome,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                reason,
                "{}"
            ),
            cancellationToken
        );

    private async Task<DemandOperationResult> DenialResultAsync(
        ActorContext actor,
        Guid companyId,
        string auditAction,
        DemandOperationState denial,
        CancellationToken cancellationToken
    ) =>
        await DeniedAsync(actor, companyId, auditAction, denial, cancellationToken);

    /// <summary>Audita tentativa negada numa transação própria curta (mesma
    /// garantia do Atendimento) e devolve o resultado sem conteúdo sensível.</summary>
    private async Task<DemandOperationResult> DeniedAsync(
        ActorContext actor,
        Guid companyId,
        string auditAction,
        DemandOperationState state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await SetContextAsync(actor, companyId, cancellationToken);
            await AppendAuditAsync(actor, companyId, auditAction, null, AuditOutcome.Denied, ErrorCode(state), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("denied", ErrorCode(state));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "denial_audit_failed");
            return new(DemandOperationState.Unavailable);
        }

        return new(state);
    }

    private async Task<DemandOperationResult> ConflictResultAsync(
        ActorContext actor,
        Guid companyId,
        string auditAction,
        DemandResponse? current,
        CancellationToken cancellationToken
    )
    {
        var result = await DeniedAsync(actor, companyId, auditAction, DemandOperationState.IntegrityConflict, cancellationToken);
        return result.State == DemandOperationState.IntegrityConflict ? new(DemandOperationState.IntegrityConflict, Current: current) : result;
    }

    private static bool IsValid(CreateDemandCommand command) =>
        NotBlank(command.Title, 200)
        && NotBlank(command.Description, 4000)
        && DemandPriorities.All.Contains(command.Priority?.Trim(), StringComparer.Ordinal)
        && command.ResponsibleMembershipId != Guid.Empty;

    private static bool IsValid(CreateRoutineCommand command) =>
        NotBlank(command.Name, 120)
        && (command.Description is null || command.Description.Trim().Length <= 500)
        && (command.Tipo is null || command.Tipo.Trim().Length <= 100)
        && (command.Departamento is null || command.Departamento.Trim().Length <= 100)
        && command.RoutingOrder >= 0
        && command.Stages.Count > 0;

    private static bool NotBlank(string? value, int maxLength) =>
        value is not null && value.Trim().Length is > 0 && value.Trim().Length <= maxLength;

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? NullIfEmpty(Guid? value) => value == Guid.Empty ? null : value;

    private static string FirstNonEmpty(string? left, string? right)
    {
        if (!string.IsNullOrWhiteSpace(left))
        {
            return left.Trim();
        }

        return right is not null ? right.Trim() : string.Empty;
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (
                inner is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(postgresException.ConstraintName, IdempotencyRecordConfiguration.KeyIndexDatabaseName, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string ErrorCode(DemandOperationState state) =>
        state switch
        {
            DemandOperationState.MembershipRequired => "membership_required",
            DemandOperationState.CapabilityDenied => "capability_denied",
            DemandOperationState.ResourceNotFound => "resource_not_found",
            DemandOperationState.IllegalTransition => "illegal_transition",
            DemandOperationState.RoutineInvalid => "routine_invalid",
            DemandOperationState.ResumptionDeadlineInvalid => "resumption_deadline_invalid",
            DemandOperationState.IdempotencyConflict => "idempotency_conflict",
            DemandOperationState.IntegrityConflict => "integrity_conflict",
            _ => "demandas_unavailable",
        };

    private void LogInformation(string result, string reason) =>
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
        logger.LogInformation(
            "Demandas {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            result,
            reason
        );
#pragma warning restore CA1873

    private void LogUnavailable(Exception? exception, string reason) =>
        logger.LogError(
            exception,
            "Demandas {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            reason
        );

    // ================= Mapeamentos =================

    public static string DisplayNumber(long demandNumber) => $"DEM-{demandNumber:D6}";

    private static DemandBoardCardResponse ToBoardCard(Demand demand, DateTimeOffset now)
    {
        var effectiveDeadline = DemandRules.EffectiveDeadline(demand.ManualDueAt, demand.SlaDueAt, demand.StageDueAt);
        var isClosed = demand.Stage == DemandStages.Closed || demand.ClosedAt.HasValue;
        return new DemandBoardCardResponse(
            demand.Id,
            demand.DemandNumber,
            DisplayNumber(demand.DemandNumber),
            demand.Title,
            demand.Priority,
            demand.Stage,
            demand.ResponsibleMembershipId,
            effectiveDeadline,
            demand.StageStartedAt ?? demand.CreatedAt,
            DemandRules.IsOverdue(effectiveDeadline, now, demand.AutomationPaused, isClosed),
            demand.AutomationPaused,
            demand.RowVersion
        );
    }

    private static DemandResponse ToResponse(Demand demand, DateTimeOffset now)
    {
        var effectiveDeadline = DemandRules.EffectiveDeadline(demand.ManualDueAt, demand.SlaDueAt, demand.StageDueAt);
        var isClosed = demand.Stage == DemandStages.Closed || demand.ClosedAt.HasValue;
        return new DemandResponse(
            demand.Id,
            demand.CompanyId,
            demand.DemandNumber,
            DisplayNumber(demand.DemandNumber),
            demand.Title,
            demand.Description,
            demand.Priority,
            demand.Stage,
            demand.RoutineId,
            demand.ResponsibleMembershipId,
            demand.Tipo,
            demand.Departamento,
            demand.ManualDueAt,
            demand.SlaDueAt,
            demand.StageDueAt,
            effectiveDeadline,
            demand.AutomationPaused,
            DemandRules.IsOverdue(effectiveDeadline, now, demand.AutomationPaused, isClosed),
            demand.StageCycleCount,
            demand.RowVersion
        );
    }

    private static RoutineResponse ToRoutineResponse(DemandRoutine routine, List<DemandRoutineStage> stages) =>
        new(
            routine.Id,
            routine.CompanyId,
            routine.Name,
            routine.Description,
            routine.Tipo,
            routine.Departamento,
            routine.AutoAssigneeMembershipId,
            routine.RoutingOrder,
            routine.IsActive,
            routine.RowVersion,
            [.. stages.Select(stage => new RoutineStageResponse(stage.Id, stage.OrderIndex, stage.Stage, stage.TriggerKind, stage.TriggerHours, stage.DeadlineHours))]
        );

    private sealed record PayloadRef(Guid DemandOrRoutineId, object Command);

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
