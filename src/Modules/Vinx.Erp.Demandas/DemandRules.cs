using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Demandas;

/// <summary>Veredito da tentava de avanço explícito a partir do gatilho vigente.</summary>
public enum DemandAdvanceVerdict
{
    LegalExplicit,
    IllegalChecklistTriggered,
    IllegalTimeTriggered,
    IllegalTerminalStage,
}

/// <summary>Critério operacional que reinicia o fluxo quando alterado.</summary>
public sealed record OperationalSnapshot(
    Guid ResponsibleMembershipId,
    Guid RoutineId,
    string? Tipo,
    string? Departamento
);

/// <summary>Regras puras do fluxo automatizado de Demandas (porta honesta do
/// kanban 0055/0056). Sem I/O, sem relógio embutido: todo instante relevante
/// entra como parâmetro para manter o teste unitário determinístico.</summary>
public static partial class DemandRules
{
    /// <summary>
    /// Mudança operacional que reinicia o fluxo: responsável, rotina, tipo ou
    /// departamento (título/descrição/prioridade/prazo nunca reiniciam).
    /// </summary>
    public static bool RequiresReInitiation(OperationalSnapshot before, OperationalSnapshot after)
    {
        if (before.ResponsibleMembershipId != after.ResponsibleMembershipId
            || before.RoutineId != after.RoutineId)
        {
            return true;
        }

        return !SameCriterion(before.Tipo, after.Tipo) || !SameCriterion(before.Departamento, after.Departamento);
    }

    private static bool SameCriterion(string? left, string? right) =>
        string.Equals(NormalizeCriterion(left), NormalizeCriterion(right), StringComparison.Ordinal);

    private static string? NormalizeCriterion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Legalidade de transição: cada etapa tem exatamente um gatilho e o comando
    /// manual só é legal no gatilho de conclusão explícita (paridade com
    /// <c>concluir_etapa_demanda</c>). Fluxo sem rotina usa a cadeia linear padrão.
    /// </summary>
    public static DemandAdvanceVerdict CheckAdvance(
        string currentStage,
        IReadOnlyList<DemandStageRule>? stageRules
    )
    {
        if (string.Equals(currentStage, DemandStages.Closed, StringComparison.Ordinal))
        {
            return DemandAdvanceVerdict.IllegalTerminalStage;
        }

        var trigger = FindRule(stageRules, currentStage)?.TriggerKind
            ?? DemandStageTriggerKind.ExplicitCompletion;
        return trigger switch
        {
            DemandStageTriggerKind.ChecklistCompleted =>
                DemandAdvanceVerdict.IllegalChecklistTriggered,
            DemandStageTriggerKind.TimeElapsed => DemandAdvanceVerdict.IllegalTimeTriggered,
            _ => DemandAdvanceVerdict.LegalExplicit,
        };
    }

    /// <summary>
    /// Próxima etapa pela ordem da rotina; sem rotina segue initiation →
    /// execution → awaiting_approval → closed; após a última etapa, closed.
    /// </summary>
    public static string ResolveNextStage(
        IReadOnlyList<DemandStageRule>? stageRules,
        string currentStage
    )
    {
        if (
            string.Equals(currentStage, DemandStages.Closed, StringComparison.Ordinal)
            || stageRules is not { Count: > 0 }
        )
        {
            return DefaultNextStage(currentStage);
        }

        var ordered = stageRules.OrderBy(rule => rule.OrderIndex).ToArray();
        var currentIndex = Array.FindIndex(ordered, rule => rule.Stage == currentStage);
        if (currentIndex < 0)
        {
            // Demanda apontando para etapa inexistente na rotina vigente (rotina
            // trocada por configuração): retoma do início ordenado.
            return ordered[0].Stage;
        }

        return currentIndex + 1 < ordered.Length ? ordered[currentIndex + 1].Stage : DemandStages.Closed;
    }

    /// <summary>Gatilho vigente da etapa corrente; conclusão explícita por omissão.</summary>
    public static DemandStageTriggerKind StageTrigger(
        IReadOnlyList<DemandStageRule>? stageRules,
        string currentStage
    ) =>
        FindRule(stageRules, currentStage)?.TriggerKind
        ?? DemandStageTriggerKind.ExplicitCompletion;

    private static string DefaultNextStage(string currentStage) =>
        currentStage switch
        {
            DemandStages.Initiation => DemandStages.Execution,
            DemandStages.Execution => DemandStages.AwaitingApproval,
            _ => DemandStages.Closed,
        };

    private static DemandStageRule? FindRule(
        IReadOnlyList<DemandStageRule>? stageRules,
        string stage
    ) =>
        stageRules?.FirstOrDefault(rule =>
            string.Equals(rule.Stage, stage, StringComparison.Ordinal)
        );

    /// <summary>Checklist completo exige pelo menos um item e todos marcados.</summary>
    public static bool IsChecklistComplete(IReadOnlyList<DemandChecklistItemState> items) =>
        items.Count > 0 && items.All(item => item.IsDone);

    /// <summary>Prazo efetivo: o primeiro vencimento entre manual, SLA e etapa.</summary>
    public static DateTimeOffset? EffectiveDeadline(
        DateTimeOffset? manualDueAt,
        DateTimeOffset? slaDueAt,
        DateTimeOffset? stageDueAt
    )
    {
        var candidates = new[] { manualDueAt, slaDueAt, stageDueAt }
            .Where(deadline => deadline.HasValue)
            .Select(deadline => deadline!.Value)
            .ToArray();
        return candidates.Length == 0 ? null : candidates.Min();
    }

    /// <summary>
    /// Atraso é DERIVADO (nunca estágio): venceu quem tem prazo efetivo passado,
    /// não fechou e não pausou automação. Empate com "agora" ainda não é atraso.
    /// </summary>
    public static bool IsOverdue(
        DateTimeOffset? EffectiveDeadline,
        DateTimeOffset Now,
        bool AutomationPaused,
        bool IsClosed
    ) =>
        EffectiveDeadline.HasValue
        && EffectiveDeadline.Value < Now
        && !AutomationPaused
        && !IsClosed;

    /// <summary>Pausa para gatilhos de tempo; demandas fechadas não pausam.</summary>
    public static bool CanPauseAutomation(bool IsClosed) => !IsClosed;

    /// <summary>Retomada de atrasada exige flag de atraso e novo prazo futuro.</summary>
    public static bool CanResumeOverdue(bool IsOverdue, DateTimeOffset NewDueAt, DateTimeOffset Now) =>
        IsOverdue && NewDueAt > Now;
}

public static partial class DemandRules
{
    // ---- Roteamento (bloco movido: mesma classe do núcleo puro) ----

    /// <summary>
    /// Precedência do kanban 0055: tipo+departamento &gt; apenas um critério &gt;
    /// curinga; empate de especificidade resolve por routing_order e depois por
    /// criação mais antiga. Critério nulo na candidata sempre casa; critério
    /// preenchido exige igualdade normalizada com o valor informado.
    /// </summary>
    public static DemandRoutingCandidate? RouteRoutine(
        IReadOnlyList<DemandRoutingCandidate> candidates,
        string? tipo,
        string? departamento
    )
    {
        var normalizedTipo = Normalize(tipo);
        var normalizedDepartamento = Normalize(departamento);

        DemandRoutingCandidate? winner = null;
        var winnerSpecificity = -1;
        foreach (var candidate in candidates)
        {
            if (!Matches(candidate.Tipo, normalizedTipo) || !Matches(candidate.Departamento, normalizedDepartamento))
            {
                continue;
            }

            var specificity = (candidate.Tipo is not null ? 1 : 0)
                + (candidate.Departamento is not null ? 1 : 0);
            if (
                winner is null
                || specificity > winnerSpecificity
                || (
                    specificity == winnerSpecificity
                    && WinsTieBreak(candidate, winner)
                )
            )
            {
                winner = candidate;
                winnerSpecificity = specificity;
            }
        }

        return winner;
    }

    private static bool WinsTieBreak(DemandRoutingCandidate challenger, DemandRoutingCandidate incumbent) =>
        challenger.RoutingOrder != incumbent.RoutingOrder
            ? challenger.RoutingOrder < incumbent.RoutingOrder
            : challenger.CreatedAt != incumbent.CreatedAt
                ? challenger.CreatedAt < incumbent.CreatedAt
                : challenger.Id.CompareTo(incumbent.Id) < 0;

    private static bool Matches(string? criterionValue, string? inputValue) =>
        criterionValue is null
        || (inputValue is not null && string.Equals(Normalize(criterionValue), inputValue, StringComparison.Ordinal));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

/// <summary>Etapa configurável de rotina vista pelas regras puras.</summary>
public sealed record DemandStageRule(
    Guid Id,
    int OrderIndex,
    string Stage,
    DemandStageTriggerKind TriggerKind,
    decimal? TriggerHours,
    decimal? DeadlineHours
);

/// <summary>Candidata a roteamento de uma demanda nova (rotina ativa da empresa).</summary>
public sealed record DemandRoutingCandidate(
    Guid Id,
    string? Tipo,
    string? Departamento,
    int RoutingOrder,
    DateTimeOffset CreatedAt
);

/// <summary>Estado mínimo de item de checklist para avaliar 100%.</summary>
public sealed record DemandChecklistItemState(bool IsDone);

public static partial class DemandRules
{
    public static string EncodeBoardCursor(DateTimeOffset occurredAt, Guid id) =>
        BoardCursor.Encode(occurredAt, id);

    public static bool TryDecodeBoardCursor(string? value, out (DateTimeOffset OccurredAt, Guid Id) decoded) =>
        BoardCursor.TryDecode(value, out decoded);
}
