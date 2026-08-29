using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Demandas;

public sealed record CreateDemandCommand(
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description,
    string Priority,
    Guid? ResponsibleMembershipId,
    Guid? RoutineId = null,
    Guid? RequesterUserId = null,
    string? Tipo = null,
    string? Departamento = null,
    DateTimeOffset? ManualDueAt = null
);

public sealed record AdvanceStageCommand(Guid CompanyId, Guid RequestId, long ExpectedVersion);

public sealed record CompleteChecklistItemCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    Guid ItemId
);

public sealed record ReassignResponsibleCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    Guid NewResponsibleMembershipId
);

public sealed record PauseAutomationCommand(Guid CompanyId, Guid RequestId, long ExpectedVersion);

public sealed record ResumeAutomationCommand(Guid CompanyId, Guid RequestId, long ExpectedVersion);

/// <summary>Retomada de atrasada: exige novo prazo futuro (paridade do legado).</summary>
public sealed record ResumeOverdueCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    DateTimeOffset NewDueAt
);

public sealed record AddParticipantCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    Guid UserId,
    bool AsSupervisor
);

public sealed record RemoveParticipantCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    Guid ParticipantId
);

public sealed record CreateRoutineCommand(
    Guid CompanyId,
    Guid RequestId,
    string Name,
    string? Description,
    string? Tipo,
    string? Departamento,
    Guid? AutoAssigneeMembershipId,
    int RoutingOrder,
    IReadOnlyList<string> DefaultChecklistItems,
    IReadOnlyList<CreateRoutineStageInput> Stages
);

public sealed record CreateRoutineStageInput(
    int OrderIndex,
    string Stage,
    DemandStageTriggerKind TriggerKind,
    decimal? TriggerHours,
    decimal? DeadlineHours
);

public sealed record UpdateRoutineStatusCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    bool IsActive
);
