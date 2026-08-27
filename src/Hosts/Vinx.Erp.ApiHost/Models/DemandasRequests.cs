using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.ApiHost.Models;

public sealed class CreateDemandRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(4000)]
    public string Description { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(12)]
    public string Priority { get; init; } = string.Empty;

    [Required]
    [NonEmptyGuid]
    public Guid ResponsibleMembershipId { get; init; }

    public Guid? RoutineId { get; init; }

    public Guid? RequesterUserId { get; init; }

    [MaxLength(100)]
    public string? Tipo { get; init; }

    [MaxLength(100)]
    public string? Departamento { get; init; }

    public DateTimeOffset? ManualDueAt { get; init; }

    public CreateDemandCommand ToCommand() =>
        new(
            CompanyId,
            RequestId,
            Title,
            Description,
            Priority,
            ResponsibleMembershipId,
            RoutineId,
            RequesterUserId,
            Tipo,
            Departamento,
            ManualDueAt
        );
}

public sealed class AdvanceDemandStageRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}

public sealed class CompleteChecklistItemRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid ItemId { get; init; }
}

public sealed class ReassignResponsibleRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid NewResponsibleMembershipId { get; init; }
}

public sealed class PauseAutomationRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}

public sealed class ResumeAutomationRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}

public sealed class ResumeOverdueRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    public DateTimeOffset NewDueAt { get; init; }
}

public sealed class AddParticipantRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid UserId { get; init; }

    public bool AsSupervisor { get; init; }
}

public sealed class RemoveParticipantRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid ParticipantId { get; init; }
}

public sealed class CreateRoutineRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [MinLength(3)]
    [MaxLength(120)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }

    [MaxLength(100)]
    public string? Tipo { get; init; }

    [MaxLength(100)]
    public string? Departamento { get; init; }

    public Guid? AutoAssigneeMembershipId { get; init; }

    [Range(0, int.MaxValue)]
    public int RoutingOrder { get; init; } = 100;

    public IReadOnlyList<string> DefaultChecklistItems { get; init; } = [];

    [Required]
    [MinLength(1)]
    public IReadOnlyList<CreateRoutineStageModel> Stages { get; init; } = [];

    public CreateRoutineCommand ToCommand() =>
        new(
            CompanyId,
            RequestId,
            Name,
            Description,
            Tipo,
            Departamento,
            AutoAssigneeMembershipId,
            RoutingOrder,
            DefaultChecklistItems,
            [
                .. Stages.Select(stage => new CreateRoutineStageInput(
                    stage.OrderIndex,
                    stage.Stage,
                    stage.TriggerKind,
                    stage.TriggerHours,
                    stage.DeadlineHours
                )),
            ]
        );
}

public sealed class CreateRoutineStageModel
{
    [Range(0, int.MaxValue)]
    public int OrderIndex { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(30)]
    public string Stage { get; init; } = string.Empty;

    public DemandStageTriggerKind TriggerKind { get; init; } =
        DemandStageTriggerKind.ExplicitCompletion;

    [Range(0.01, 1_000_000)]
    public decimal? TriggerHours { get; init; }

    [Range(0.01, 1_000_000)]
    public decimal? DeadlineHours { get; init; }
}

public sealed class UpdateRoutineStatusRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    public bool IsActive { get; init; }
}

internal sealed record DemandConflictResponse(string Code, DemandResponse? Current);

public sealed class ApprovalDecisionRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}
