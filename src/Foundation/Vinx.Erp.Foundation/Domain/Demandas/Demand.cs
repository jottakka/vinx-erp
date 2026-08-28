namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Demanda do fluxo operacional interno. O atraso é estado DERIVADO
/// (<see cref="OverdueSince"/> alimentado pelo worker; nunca um estágio) e o
/// <see cref="StageCycleCount"/> cresce a cada re-iniciação para dar chave de
/// deduplicação nova aos avisos por ciclo.
/// </summary>
public sealed class Demand : IConcurrencyVersioned
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>Número sequencial do banco (GENERATED ALWAYS) exibido como DEM-…</summary>
    public long DemandNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>critical | high | medium | low.</summary>
    public string Priority { get; set; } = DemandPriorities.Medium;

    /// <summary>Um dos <see cref="DemandStages"/>.</summary>
    public string Stage { get; set; } = DemandStages.Initiation;

    public Guid? RoutineId { get; set; }

    public Guid ResponsibleMembershipId { get; set; }

    public Guid RequesterUserId { get; set; }

    public string? Tipo { get; set; }

    public string? Departamento { get; set; }

    /// <summary>Prazo manual informado na demanda.</summary>
    public DateTimeOffset? ManualDueAt { get; set; }

    /// <summary>Prazo derivado da prioridade (SLA).</summary>
    public DateTimeOffset? SlaDueAt { get; set; }

    /// <summary>Prazo máximo da etapa corrente da rotina.</summary>
    public DateTimeOffset? StageDueAt { get; set; }

    public DateTimeOffset? StageStartedAt { get; set; }

    /// <summary>Pausa apenas os gatilhos de tempo; operações manuais seguem válidas.</summary>
    public bool AutomationPaused { get; set; }

    /// <summary>Momento em que o worker detectou vencimento; nulo enquanto não vencida.</summary>
    public DateTimeOffset? OverdueSince { get; set; }

    /// <summary>Ciclo corrente de iniciação; incrementa a cada re-iniciação.</summary>
    public int StageCycleCount { get; set; } = 1;

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Aprovação pendente vinculada quando a etapa exige decisão.</summary>
    public Guid? PendingApprovalId { get; set; }

    public Guid? PendingProposalId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid UpdatedByUserId { get; set; }

    public Guid RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedByUserId { get; set; }

    public string? DeletedReason { get; set; }

    public long RowVersion { get; set; }
}

public static class DemandPriorities
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";

    public static readonly IReadOnlyList<string> All = [Critical, High, Medium, Low];
}
