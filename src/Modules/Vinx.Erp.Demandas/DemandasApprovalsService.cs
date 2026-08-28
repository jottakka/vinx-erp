using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Approvals;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Demandas;

/// <summary>
/// Consumo do estágio awaiting_approval via serviços públicos do Foundation.
/// Elegibilidade é resolvida SEMPRE no servidor, DEPOIS de travar a linha da
/// demanda: exige a capability documentada de decisão
/// (demandas.demand.advance, a MESMA do preparo e da confirmação Foundation) e
/// o criador da entrega NUNCA decide a própria demanda; papéis nominais
/// (supervisor) nem assign sozinhos decidem. O preparo acontece quando a
/// demanda entra na etapa e guarda o vínculo da proposta na própria demanda —
/// sem alçada clonada: a política vive no Foundation.
/// </summary>
public sealed class DemandasApprovalsService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry,
    CapabilityEvaluator capabilityEvaluator,
    DemandasNotificationComposer notifications,
    ILogger<PrepareApprovalService> prepareLogger,
    ILogger<ConfirmApprovalService> confirmLogger,
    ILogger<DemandasApprovalsService> logger
)
{
    private const string PolicyVersion = "demand.v1";
    private const string Module = "demandas";

    private static readonly ApprovalCapabilityPolicy Policy =
        ApprovalCapabilityPolicy.ForModule("demandas", KnownCapabilities.DemandasDemandAdvance, AuditSource.Web);

    public sealed record DecisionCommand(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion
    );

    /// <summary>Prepara a proposta durável quando a demanda entra em awaiting_approval.</summary>
    internal async Task<bool> PrepareOnStageEntryAsync(
        ActorContext actor,
        Demand demand,
        Guid requestId,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new { demandId = demand.Id, cycle = demand.StageCycleCount }
        );
        // Fronteira por recurso: a proposta viva é única por (ator, empresa,
        // demanda) — dois avanços do mesmo ator em demandas distintas podem
        // aguardar aprovação simultaneamente; o legado AI mantém a chave vazia.
        var prepare = new PrepareApprovalCommand(
            demand.CompanyId,
            requestId,
            "demanda.aprovar-etapa",
            payload,
            PolicyVersion,
            resourceKey: $"demandas.demand:{demand.Id:N}:{demand.StageCycleCount}"
        );
        var service = NewPrepareService();
        var result = await service.PrepareAsync(actor, prepare, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "Demandas {Operation} {Module} {Result} {Reason} {ErrorCode}",
                "approval_prepare",
                Module,
                "failed",
                "prepare_denied",
                result.ErrorCode?.ToString() ?? "unknown"
            );
            return false;
        }

        demand.PendingApprovalId = result.Value.ApprovalId;
        demand.PendingProposalId = result.Value.ProposalId;
        return true;
    }

    public async Task<DemandOperationResult> DecideAsync(
        ActorContext actor,
        Guid demandId,
        bool approve,
        DecisionCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (
            command.CompanyId == Guid.Empty
            || command.RequestId == Guid.Empty
            || command.ExpectedVersion <= 0
        )
        {
            return new(DemandOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, command.CompanyId, cancellationToken);

            var membership = await database.Memberships.SingleOrDefaultAsync(
                candidate => candidate.CompanyId == command.CompanyId && candidate.UserId == actor.UserId,
                cancellationToken);
            if (membership is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, command.RequestId, DemandOperationState.MembershipRequired, cancellationToken);
            }

            // A trava da demanda vem ANTES da elegibilidade: remoção de
            // supervisão ou revogação cometida antes da trava é observada pela
            // reavaliação server-side abaixo, sem janela TOCTOU.
            var demand = await database.Demands.FromSqlInterpolated(
                    $"SELECT * FROM demandas.demands WHERE company_id = {command.CompanyId} AND id = {demandId} AND deleted_at IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (demand is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, command.RequestId, DemandOperationState.ResourceNotFound, cancellationToken);
            }

            var eligibility = await EligibilityAsync(actor, command.CompanyId, demand, membership.Id, cancellationToken);
            if (eligibility is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, command.RequestId, eligibility.Value, cancellationToken);
            }

            if (demand.RowVersion != command.ExpectedVersion || demand.Stage != DemandStages.AwaitingApproval)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, command.RequestId, approve ? DemandOperationState.IntegrityConflict : DemandOperationState.IllegalTransition, cancellationToken);
            }

            if (
                demand.PendingApprovalId is not { } approvalId
                || demand.PendingProposalId is not { } proposalId
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return await DeniedAsync(actor, command.CompanyId, command.RequestId, DemandOperationState.Invalid, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            DemandOperationState outcome;
            if (approve)
            {
                var payload = JsonSerializer.SerializeToElement(new { demandId, cycle = demand.StageCycleCount });
                var confirm = new ConfirmApprovalApprovalShim(
                    command.CompanyId,
                    approvalId,
                    proposalId,
                    command.RequestId,
                    payload,
                    PolicyVersion
                );
                var confirmResult = await NewConfirmService().ConfirmAsync(actor, confirm.ToFoundation(), cancellationToken);
                if (confirmResult.Value is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await DeniedAsync(actor, command.CompanyId, command.RequestId, DemandOperationState.IntegrityConflict, cancellationToken);
                }

                outcome = confirmResult.Value.Outcome switch
                {
                    ConfirmApprovalOutcome.Claimed or ConfirmApprovalOutcome.Replay => CloseDemand(demand, actor, command.RequestId, now),
                    ConfirmApprovalOutcome.Conflict => DemandOperationState.IntegrityConflict,
                    _ => DemandOperationState.Invalid,
                };
                if (outcome is DemandOperationState.IntegrityConflict or DemandOperationState.Invalid)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await DeniedAsync(actor, command.CompanyId, command.RequestId, outcome, cancellationToken);
                }
            }
            else
            {
                var vetoResult = await NewConfirmService().VetoAsync(
                    actor,
                    new VetoApprovalCommand(command.CompanyId, approvalId, proposalId, command.RequestId),
                    cancellationToken
                );
                if (vetoResult.Value is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await DeniedAsync(actor, command.CompanyId, command.RequestId, DemandOperationState.IntegrityConflict, cancellationToken);
                }

                outcome = vetoResult.Value.Outcome switch
                {
                    VetoApprovalOutcome.Vetoed or VetoApprovalOutcome.AlreadyVetoed =>
                        ReturnToExecution(demand, actor, command.RequestId, now),
                    VetoApprovalOutcome.Conflict => DemandOperationState.IntegrityConflict,
                    _ => DemandOperationState.Invalid,
                };
                if (outcome is DemandOperationState.IntegrityConflict or DemandOperationState.Invalid)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await DeniedAsync(actor, command.CompanyId, command.RequestId, outcome, cancellationToken);
                }
            }

            await auditAppender.AppendAsync(ApprovalDecisionAudit(actor, command, demand.Id, approve ? "demandas.approval.approve" : "demandas.approval.reject", AuditOutcome.Success), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Recomposição pós-commit (sem reabrir transação): fechado encerra o
            // fluxo; veto reinicia para execution com ciclo novo.
            var recipients = await RecipientsForStageChangeAsync(demand, command.CompanyId, cancellationToken);
            await notifications.ComposeAsync(
                actor,
                command.CompanyId,
                demand.Id,
                demand.StageCycleCount,
                DemandNotificationKinds.StageAdvanced,
                recipients,
                CancellationToken.None
            );

            logger.LogInformation(
                "Demandas {Operation} {Module} {Result} {Reason}",
                "approval_decision",
                Module,
                "succeeded",
                approve ? "approved" : "rejected"
            );
            return new(DemandOperationState.Success, Demand: ToDemandSummary(demand));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(exception, "Demandas {Operation} {Module} {Result} {Reason}", OperationName(), Module, "unavailable", "database_or_audit_failed");
            return new(DemandOperationState.Unavailable);
        }

        static string OperationName() => "demandas_decision";
    }

    private DemandOperationState CloseDemand(Demand demand, ActorContext actor, Guid requestId, DateTimeOffset now)
    {
        AppendEvent(actor, demand, "approval_granted", $"approve:{requestId:N}:{demand.StageCycleCount}", now, fromStage: DemandStages.AwaitingApproval, toStage: DemandStages.Closed);
        demand.Stage = DemandStages.Closed;
        demand.ClosedAt = now;
        demand.PendingApprovalId = null;
        demand.PendingProposalId = null;
        TouchDemand(demand, actor, requestId);
        return DemandOperationState.Success;
    }

    private DemandOperationState ReturnToExecution(Demand demand, ActorContext actor, Guid requestId, DateTimeOffset now)
    {
        // Veto é re-iniciação funcional: ciclo ANTES do evento/aviso, senão o
        // dedupe por chave de ciclo suprime todos os avisos seguintes.
        demand.StageCycleCount += 1;
        AppendEvent(actor, demand, "approval_rejected", $"reject:{requestId:N}:{demand.StageCycleCount}", now, fromStage: DemandStages.AwaitingApproval, toStage: DemandStages.Execution);
        demand.Stage = DemandStages.Execution;
        demand.StageStartedAt = now;
        demand.PendingApprovalId = null;
        demand.PendingProposalId = null;
        TouchDemand(demand, actor, requestId);
        return DemandOperationState.Success;
    }

    private void AppendEvent(ActorContext actor, Demand demand, string kind, string token, DateTimeOffset at, string? fromStage = null, string? toStage = null)
    {
        database.DemandEvents.Add(new DemandEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = demand.CompanyId,
            DemandId = demand.Id,
            Kind = kind,
            FromStage = fromStage,
            ToStage = toStage,
            CycleCount = demand.StageCycleCount,
            OccurrenceToken = token,
            ActorUserId = actor.UserId,
            MetadataJson = "{}",
            OccurredAt = at,
        });
    }

    private void TouchDemand(Demand demand, ActorContext actor, Guid requestId)
    {
        demand.UpdatedByUserId = actor.UserId;
        demand.RequestId = requestId;
        var updatedAtEntry = database.Entry(demand).Property(entity => entity.UpdatedAt);
        updatedAtEntry.CurrentValue = DateTimeOffset.UtcNow;
        updatedAtEntry.IsModified = true;
    }

    /// <summary>
    /// Decisão exige a MESMA capability documentada do preparo
    /// (demandas.demand.advance, escopo company) — módulo e Foundation
    /// alinhados — e o autor da entrega NUNCA decide a própria demanda.
    /// Papéis nominais (supervisor) ou assign sozinhos não decidem.
    /// </summary>
    private async Task<DemandOperationState?> EligibilityAsync(
        ActorContext actor,
        Guid companyId,
        Demand demand,
        Guid membershipId,
        CancellationToken cancellationToken
    )
    {
        if (demand.CreatedByUserId == actor.UserId)
        {
            return DemandOperationState.CapabilityDenied;
        }

        var canDecide = await capabilityEvaluator.HasGrantAsync(
            companyId,
            membershipId,
            new CapabilityRequirement(KnownCapabilities.DemandasDemandAdvance, CapabilityScope.Company),
            cancellationToken);
        return canDecide ? null : DemandOperationState.CapabilityDenied;
    }

    private async Task<Guid[]> RecipientsForStageChangeAsync(Demand demand, Guid companyId, CancellationToken cancellationToken)
    {
        var responsibleUser = await database.Memberships
            .Where(candidate => candidate.CompanyId == companyId && candidate.Id == demand.ResponsibleMembershipId)
            .Select(candidate => (Guid?)candidate.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        var participantUsers = await database.DemandParticipants.AsNoTracking()
            .Where(participant => participant.CompanyId == companyId && participant.DemandId == demand.Id && participant.DeletedAt == null)
            .Select(participant => participant.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var all = new List<Guid?>();
        if (responsibleUser.HasValue)
        {
            all.Add(responsibleUser.Value);
        }
        all.AddRange(participantUsers.Select(user => (Guid?)user));
        return [.. all.Where(user => user.HasValue).Select(user => user!.Value).Distinct()];
    }

    private static AuditEventData ApprovalDecisionAudit(
        ActorContext actor,
        DecisionCommand command,
        Guid demandId,
        string action,
        AuditOutcome outcome
    ) =>
        new(actor.UserId, AuditScope.Company, command.CompanyId, AuditSource.Web, action, "demand", demandId, outcome, new CorrelationContext(actor.CorrelationId).CorrelationId, null, "{}");

    private PrepareApprovalService NewPrepareService() =>
        new(database, auditAppender, telemetry, capabilityEvaluator, prepareLogger, Policy);

    private ConfirmApprovalService NewConfirmService() =>
        new(database, auditAppender, telemetry, capabilityEvaluator, confirmLogger, Policy);

    private static DemandResponse ToDemandSummary(Demand demand) =>
        new(demand.Id,
            demand.CompanyId,
            demand.DemandNumber,
            DemandasService.DisplayNumber(demand.DemandNumber),
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
            DemandRules.EffectiveDeadline(demand.ManualDueAt, demand.SlaDueAt, demand.StageDueAt),
            demand.AutomationPaused,
            false,
            demand.StageCycleCount,
            demand.RowVersion);

    private async Task<DemandOperationResult> DeniedAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        DemandOperationState state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(new AuditEventData(actor.UserId, AuditScope.Company, companyId, AuditSource.Web, "demandas.approval.decision", "demand", null, AuditOutcome.Denied, new CorrelationContext(actor.CorrelationId).CorrelationId, ErrorCode(state), "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(exception, "Demandas {Operation} {Module} {Result} {Reason}", "approval_decision", Module, "unavailable", "denial_audit_failed");
            return new(DemandOperationState.Unavailable);
        }

        return new(state);
    }

    private static string ErrorCode(DemandOperationState state) =>
        state switch
        {
            DemandOperationState.MembershipRequired => "membership_required",
            DemandOperationState.CapabilityDenied => "capability_denied",
            DemandOperationState.ResourceNotFound => "resource_not_found",
            DemandOperationState.IllegalTransition => "illegal_transition",
            DemandOperationState.IntegrityConflict => "integrity_conflict",
            _ => "demandas_unavailable",
        };

    // Wrapper de compatibilidade: mantém a ordem canônica dos parâmetros.
    private sealed record ConfirmApprovalApprovalShim(
        Guid CompanyId,
        Guid ApprovalId,
        Guid ProposalId,
        Guid RequestId,
        System.Text.Json.JsonElement Payload,
        string PolicyVersion
    )
    {
        public ConfirmApprovalCommand ToFoundation() =>
            new(CompanyId, ApprovalId, ProposalId, RequestId, Payload, PolicyVersion);
    }
}
