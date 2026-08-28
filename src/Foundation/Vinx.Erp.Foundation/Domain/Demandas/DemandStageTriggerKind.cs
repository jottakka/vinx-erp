namespace Vinx.Erp.Foundation.Domain;

/// <summary>Gatilho de avanço de uma etapa. Cada etapa tem exatamente um gatilho.</summary>
public enum DemandStageTriggerKind
{
    /// <summary>Avanço por conclusão explícita do responsável.</summary>
    ExplicitCompletion = 1,

    /// <summary>Avanço automático quando o checklist da demanda atinge 100%.</summary>
    ChecklistCompleted = 2,

    /// <summary>Avanço pelo worker quando decorrido o tempo configurado.</summary>
    TimeElapsed = 3,
}

/// <summary>Vocabulário honesto de estágios (nunca as chaves trocadas do legado).</summary>
public static class DemandStages
{
    public const string Initiation = "initiation";
    public const string Execution = "execution";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Closed = "closed";

    public static readonly IReadOnlyList<string> All =
        [Initiation, Execution, AwaitingApproval, Closed];
}
