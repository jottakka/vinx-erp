using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Demandas.UnitTests;

/// <summary>
/// Contratos puros do fluxo automatizado de Demandas (espelho do kanban 0055
/// rebatizado com vocabulário honesto: initiation/execution/awaiting_approval/closed).
/// </summary>
public sealed class DemandRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static DemandStageRule Rule(
        int Order,
        string Stage,
        DemandStageTriggerKind TriggerKind,
        decimal? TriggerHours = null,
        decimal? DeadlineHours = null
    ) => new(Guid.NewGuid(), Order, Stage, TriggerKind, TriggerHours, DeadlineHours);

    // ---------- Matriz de legalidade de transição ----------

    [Fact]
    public void Explicit_advance_is_legal_when_stage_trigger_is_explicit_completion()
    {
        var rules = new[]
        {
            Rule(0, "initiation", DemandStageTriggerKind.ExplicitCompletion),
            Rule(1, "execution", DemandStageTriggerKind.ExplicitCompletion),
        };

        Assert.Equal(
            DemandAdvanceVerdict.LegalExplicit,
            DemandRules.CheckAdvance("execution", rules)
        );
    }

    [Fact]
    public void Explicit_advance_is_rejected_for_checklist_triggered_stage()
    {
        var rules = new[] { Rule(0, "initiation", DemandStageTriggerKind.ChecklistCompleted) };

        Assert.Equal(
            DemandAdvanceVerdict.IllegalChecklistTriggered,
            DemandRules.CheckAdvance("initiation", rules)
        );
    }

    [Fact]
    public void Explicit_advance_is_rejected_for_time_triggered_stage()
    {
        var rules = new[]
        {
            Rule(0, "initiation", DemandStageTriggerKind.TimeElapsed, TriggerHours: 4m),
        };

        Assert.Equal(
            DemandAdvanceVerdict.IllegalTimeTriggered,
            DemandRules.CheckAdvance("initiation", rules)
        );
    }

    [Fact]
    public void Advance_on_closed_demand_is_always_terminal_illegal()
    {
        Assert.Equal(DemandAdvanceVerdict.IllegalTerminalStage, DemandRules.CheckAdvance("closed", null));
    }

    [Fact]
    public void Default_flow_without_routine_advances_by_explicit_completion()
    {
        Assert.Equal(DemandAdvanceVerdict.LegalExplicit, DemandRules.CheckAdvance("initiation", null));
        Assert.Equal(DemandAdvanceVerdict.LegalExplicit, DemandRules.CheckAdvance("execution", null));
        Assert.Equal(DemandAdvanceVerdict.LegalExplicit, DemandRules.CheckAdvance("awaiting_approval", null));
    }

    // ---------- Próxima etapa ----------

    [Theory]
    [InlineData("initiation", "execution")]
    [InlineData("execution", "awaiting_approval")]
    [InlineData("awaiting_approval", "closed")]
    public void Default_flow_follows_the_linear_honest_stages(string current, string expected)
    {
        Assert.Equal(expected, DemandRules.ResolveNextStage(null, current));
    }

    [Fact]
    public void Routine_next_stage_uses_order_index_and_closes_after_last_stage()
    {
        var rules = new[]
        {
            Rule(0, "initiation", DemandStageTriggerKind.ChecklistCompleted),
            Rule(1, "execution", DemandStageTriggerKind.ExplicitCompletion),
        };

        Assert.Equal("execution", DemandRules.ResolveNextStage(rules, "initiation"));
        Assert.Equal("closed", DemandRules.ResolveNextStage(rules, "execution"));
    }

    // ---------- Precedência de roteamento ----------

    [Fact]
    public void Routing_prefers_tipo_e_departamento_then_single_then_wildcard()
    {
        var both = new DemandRoutingCandidate(Guid.NewGuid(), "Pagamento", "Financeiro", 100, Now);
        var onlyTipo = new DemandRoutingCandidate(Guid.NewGuid(), "Pagamento", null, 5, Now);
        var wildcard = new DemandRoutingCandidate(Guid.NewGuid(), null, null, 1, Now);

        var chosen = DemandRules.RouteRoutine(
            new[] { wildcard, onlyTipo, both },
            tipo: "pagamento",
            departamento: "financeiro"
        );

        Assert.Equal(both.Id, chosen!.Id);
    }

    [Fact]
    public void Routing_single_criteria_beats_wildcard_even_with_lower_routing_order()
    {
        var onlyDepartamento = new DemandRoutingCandidate(Guid.NewGuid(), null, "Financeiro", 90, Now);
        var wildcard = new DemandRoutingCandidate(Guid.NewGuid(), null, null, 10, Now);

        var chosen = DemandRules.RouteRoutine(
            new[] { wildcard, onlyDepartamento },
            tipo: null,
            departamento: "financeiro"
        );

        Assert.Equal(onlyDepartamento.Id, chosen!.Id);
    }

    [Fact]
    public void Routing_tie_breaks_by_routing_order_then_creation()
    {
        var late = new DemandRoutingCandidate(Guid.NewGuid(), "Pagamento", null, 20, Now);
        var earlyLowOrder = new DemandRoutingCandidate(
            Guid.NewGuid(),
            "Pagamento",
            null,
            10,
            Now.AddHours(-1)
        );
        var sameOrderEarlier = new DemandRoutingCandidate(
            Guid.NewGuid(),
            "Pagamento",
            null,
            10,
            Now.AddHours(-2)
        );

        var winner = DemandRules.RouteRoutine(
            new[] { late, sameOrderEarlier, earlyLowOrder },
            tipo: "pagamento",
            departamento: null
        );

        Assert.Equal(sameOrderEarlier.Id, winner!.Id);
    }

    [Fact]
    public void Non_matching_criteria_candidates_are_not_selected()
    {
        var wrongTipo = new DemandRoutingCandidate(Guid.NewGuid(), "Compra", null, 1, Now);
        var wildcard = new DemandRoutingCandidate(Guid.NewGuid(), null, null, 2, Now);

        var chosen = DemandRules.RouteRoutine(new[] { wrongTipo, wildcard }, "pagamento", null);

        Assert.NotNull(chosen);
        Assert.Equal(wildcard.Id, chosen.Id);
    }

    [Fact]
    public void Wildcard_only_matches_when_both_criteria_are_null()
    {
        var typed = new DemandRoutingCandidate(Guid.NewGuid(), "Pagamento", "Financeiro", 100, Now);

        Assert.Null(DemandRules.RouteRoutine(new[] { typed }, "compra", "compras"));
    }

    // ---------- Re-iniciação por mudança operacional ----------

    [Fact]
    public void Responsible_routine_tipo_or_departamento_changes_require_re_initiation()
    {
        var before = new OperationalSnapshot(Guid.NewGuid(), Guid.NewGuid(), "Pagamento", "Financeiro");
        var responsible = before with { ResponsibleMembershipId = Guid.NewGuid() };
        var routine = before with { RoutineId = Guid.NewGuid() };
        var tipo = before with { Tipo = "Compra" };
        var departamento = before with { Departamento = "Compras" };

        Assert.True(DemandRules.RequiresReInitiation(before, responsible));
        Assert.True(DemandRules.RequiresReInitiation(before, routine));
        Assert.True(DemandRules.RequiresReInitiation(before, tipo));
        Assert.True(DemandRules.RequiresReInitiation(before, departamento));
    }

    [Fact]
    public void Title_description_priority_and_deadline_edits_never_reset_the_stage()
    {
        // O comparador só enxerga os campos operacionais que reiniciam o fluxo;
        // título/descrição/prioridade/prazo ficam fora da decisão (A4 adapt).
        Assert.False(DemandRules.RequiresReInitiation(Snapshot(), Snapshot()));
    }

    [Fact]
    public void Tipo_and_departamento_compare_trimmed_and_case_insensitive()
    {
        var before = new OperationalSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            " Pagamento ",
            "Financeiro"
        );
        var after = new OperationalSnapshot(
            before.ResponsibleMembershipId,
            before.RoutineId,
            "pagamento",
            "FINANCEIRO "
        );

        Assert.False(DemandRules.RequiresReInitiation(before, after));
    }

    private static readonly Guid FixedMembershipId = Guid.NewGuid();
    private static readonly Guid FixedRoutineId = Guid.NewGuid();

    private static OperationalSnapshot Snapshot()
    {
        return new OperationalSnapshot(FixedMembershipId, FixedRoutineId, "Pagamento", "Financeiro");
    }

    // ---------- Checklist ----------

    [Fact]
    public void Checklist_auto_advance_requires_every_item_done_and_at_least_one_item()
    {
        Assert.False(DemandRules.IsChecklistComplete(Array.Empty<DemandChecklistItemState>()));
        Assert.False(
            DemandRules.IsChecklistComplete(new[] { new DemandChecklistItemState(true), new(false) })
        );
        Assert.True(
            DemandRules.IsChecklistComplete(new[] { new DemandChecklistItemState(true), new(true) })
        );
    }

    // ---------- Prazo efetivo e atraso derivado ----------

    [Fact]
    public void Effective_deadline_is_the_earliest_of_manual_sla_and_stage_defaults()
    {
        var manual = Now.AddHours(24);
        var sla = Now.AddHours(8);
        var stage = Now.AddHours(48);

        Assert.Equal(sla, DemandRules.EffectiveDeadline(manual, sla, stage));
        Assert.Null(DemandRules.EffectiveDeadline(null, null, null));
    }

    [Fact]
    public void Overdue_is_derived_from_past_effective_deadline_excluding_paused_and_closed()
    {
        var pastDeadline = Now.AddHours(-1);

        Assert.True(DemandRules.IsOverdue(pastDeadline, Now, AutomationPaused: false, IsClosed: false));
        Assert.False(DemandRules.IsOverdue(pastDeadline, Now, AutomationPaused: true, IsClosed: false));
        Assert.False(DemandRules.IsOverdue(pastDeadline, Now, AutomationPaused: false, IsClosed: true));
        Assert.False(DemandRules.IsOverdue(Now.AddHours(1), Now, false, false));
        Assert.False(DemandRules.IsOverdue(null, Now, false, false));
    }

    [Fact]
    public void Deadline_equal_to_now_is_not_yet_overdue()
    {
        Assert.False(DemandRules.IsOverdue(Now, Now, false, false));
    }

    // ---------- Pausa e retomada ----------

    [Fact]
    public void Pause_is_blocked_only_for_closed_demands()
    {
        Assert.True(DemandRules.CanPauseAutomation(IsClosed: false));
        Assert.False(DemandRules.CanPauseAutomation(IsClosed: true));
    }

    [Fact]
    public void Resume_overdue_requires_overdue_state_and_future_deadline()
    {
        Assert.True(DemandRules.CanResumeOverdue(IsOverdue: true, Now.AddHours(1), Now));
        Assert.False(DemandRules.CanResumeOverdue(IsOverdue: true, Now.AddHours(-1), Now));
        Assert.False(DemandRules.CanResumeOverdue(IsOverdue: false, Now.AddHours(1), Now));
    }
}

public sealed class BoardCursorTests
{
    [Fact]
    public void Cursor_round_trips_created_at_and_id()
    {
        var at = new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var encoded = BoardCursor.Encode(at, id);

        Assert.True(BoardCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(at, decoded.OccurredAt);
        Assert.Equal(id, decoded.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!not-base64url")]
    [InlineData("AAAA")]
    public void Malformed_cursors_are_rejected(string candidate)
    {
        Assert.False(BoardCursor.TryDecode(candidate, out _));
    }
}
