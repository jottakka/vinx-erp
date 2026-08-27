using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Demandas;

public sealed record DemandResponse(
    Guid Id,
    Guid CompanyId,
    long DemandNumber,
    string DemandDisplayNumber,
    string Title,
    string Description,
    string Priority,
    string Stage,
    Guid? RoutineId,
    Guid ResponsibleMembershipId,
    string? Tipo,
    string? Departamento,
    DateTimeOffset? ManualDueAt,
    DateTimeOffset? SlaDueAt,
    DateTimeOffset? StageDueAt,
    DateTimeOffset? EffectiveDeadline,
    bool AutomationPaused,
    bool IsOverdue,
    int CycleCount,
    long Version
);

public sealed record DemandBoardCardResponse(
    Guid Id,
    long DemandNumber,
    string DemandDisplayNumber,
    string Title,
    string Priority,
    string Stage,
    Guid ResponsibleMembershipId,
    DateTimeOffset? EffectiveDeadline,
    DateTimeOffset StageStartedAt,
    bool IsOverdue,
    bool AutomationPaused,
    long Version
);

public sealed record DemandChecklistItemResponse(
    Guid Id,
    string Text,
    int Position,
    bool IsDone,
    DateTimeOffset? DoneAt
);

public sealed record DemandParticipantResponse(Guid Id, Guid UserId, string Role);

public sealed record DemandEventResponse(
    Guid Id,
    string Kind,
    string? FromStage,
    string? ToStage,
    int CycleCount,
    Guid? ActorUserId,
    DateTimeOffset OccurredAt
);

public sealed record DemandDetailResponse(
    DemandResponse Demand,
    IReadOnlyList<DemandChecklistItemResponse> Checklist,
    IReadOnlyList<DemandParticipantResponse> Participants,
    IReadOnlyList<DemandEventResponse> History
);

public sealed record RoutineStageResponse(
    Guid Id,
    int OrderIndex,
    string Stage,
    DemandStageTriggerKind TriggerKind,
    decimal? TriggerHours,
    decimal? DeadlineHours
);

public sealed record RoutineResponse(
    Guid Id,
    Guid CompanyId,
    string Name,
    string? Description,
    string? Tipo,
    string? Departamento,
    Guid? AutoAssigneeMembershipId,
    int RoutingOrder,
    bool IsActive,
    long Version,
    IReadOnlyList<RoutineStageResponse> Stages
);

public enum DemandOperationState
{
    Created,
    Replay,
    Success,
    Invalid,
    MembershipRequired,
    CapabilityDenied,
    ResourceNotFound,
    IllegalTransition,
    ResumptionDeadlineInvalid,
    RoutineInvalid,
    SupervisorConflict,
    IdempotencyConflict,
    IntegrityConflict,
    Unavailable,
}

public sealed record DemandOperationResult(
    DemandOperationState State,
    DemandResponse? Demand = null,
    DemandResponse? Current = null,
    DemandBoardPage? BoardPage = null,
    DemandDetailResponse? Detail = null,
    RoutineResponse? Routine = null,
    IReadOnlyList<RoutineResponse>? Routines = null
);

/// <summary>
/// Envelope paginado do quadro: continuação opaca por cursor (createdAt+id),
/// total para barra de progresso e hasMore explícito — nada de truncamento
/// silencioso.
/// </summary>
public sealed record DemandBoardPage(
    IReadOnlyList<DemandBoardCardResponse> Items,
    int Total,
    bool HasMore,
    string? NextCursor
);
