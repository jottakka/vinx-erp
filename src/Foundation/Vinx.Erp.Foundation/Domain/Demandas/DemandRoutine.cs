namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Rotina de Demandas: configuração por empresa que roteia demandas novas e
/// define o fluxo de etapas. Empresa-escopo, versão otimista e sem seeds —
/// catálogo construído pela própria empresa com capability dedicada.
/// </summary>
public sealed class DemandRoutine
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Critério de tipo do roteamento; nulo torna o critério curinga.</summary>
    public string? Tipo { get; set; }

    /// <summary>Critério de departamento do roteamento; nulo torna o critério curinga.</summary>
    public string? Departamento { get; set; }

    /// <summary>Responsável automático sugerido quando a demanda não traz um.</summary>
    public Guid? AutoAssigneeMembershipId { get; set; }

    /// <summary>Desempate entre rotinas de mesma especificidade (menor primeiro).</summary>
    public int RoutingOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public Guid CreatedByUserId { get; set; }

    public Guid UpdatedByUserId { get; set; }

    public Guid RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedByUserId { get; set; }

    public string? DeletedReason { get; set; }

    public long RowVersion { get; set; } = 1;
}

/// <summary>
/// Etapa de rotina com exatamente um gatilho de avanço. O estágio "closed"
/// nunca é configurável: encerramento é derivado do fim do fluxo.
/// </summary>
public sealed class DemandRoutineStage
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RoutineId { get; set; }

    public int OrderIndex { get; set; }

    /// <summary>Um dos <see cref="DemandStages"/>, nunca "closed".</summary>
    public string Stage { get; set; } = string.Empty;

    public DemandStageTriggerKind TriggerKind { get; set; } =
        DemandStageTriggerKind.ExplicitCompletion;

    /// <summary>Horas para o gatilho de tempo decorrido (obrigatório nesse gatilho).</summary>
    public decimal? TriggerHours { get; set; }

    /// <summary>Prazo máximo da etapa em horas; alimenta o prazo efetivo.</summary>
    public decimal? DeadlineHours { get; set; }
}
