using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

// Fixtures sintéticas: domínios sob .example; nenhum dado pessoal real.
public sealed class AgentOutputGateAdversarialTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid BetaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    [Fact]
    public void Tool_proposal_state_is_pure_and_carries_text_unmodified()
    {
        var gate = Gate();
        const string text = "Proposta enviada para contato@example.com";
        var response = Response(
            text: text,
            sources: [new AgentCitedSource("fonte-alfa", AlfaCompanyId)],
            confidence: 0.9,
            hasToolProposal: true);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        // requires_confirmation é ESTADO puro: Result/ok espelha a proposta e
        // libera o texto do modelo SEM transformação — o gate não redige
        // conteúdo. Estruturalmente o gate não possui caminho de execução.
        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal("ok", result.ReasonCode);
        Assert.True(result.HasToolProposal);
        Assert.NotNull(result.Text);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public async Task Concurrent_gates_keep_tenant_results_isolated()
    {
        // Gates/policies independentes por empresa; o gate é stateless por
        // chamada, então evaluates em paralelo pareiam 1:1 sem mistura.
        var alfaSource1 = new AgentCitedSource("fonte-alfa-1", AlfaCompanyId);
        var alfaSource2 = new AgentCitedSource("fonte-alfa-2", AlfaCompanyId);
        var betaSource = new AgentCitedSource("fonte-beta-1", BetaCompanyId);
        var gateAlfa = Gate();
        var gateBeta = Gate();

        var alfaTask = Task.Run(() => gateAlfa.Evaluate(new AgentOutputGateRequest(
            Context(AlfaCompanyId, "Alfa"),
            Response("Resumo operacional da Alfa", [alfaSource1, alfaSource2], 0.9),
            Contract(),
            SourcesRequired: true)));
        var betaTask = Task.Run(() => gateBeta.Evaluate(new AgentOutputGateRequest(
            Context(BetaCompanyId, "Beta"),
            Response("Resumo operacional da Beta", [betaSource], 0.9),
            Contract(),
            SourcesRequired: true)));
        await Task.WhenAll(alfaTask, betaTask);
        var alfa = await alfaTask;
        var beta = await betaTask;

        Assert.Equal(AgentOutputGateStatus.Result, alfa.Status);
        Assert.Equal("ok", alfa.ReasonCode);
        Assert.Equal([alfaSource1, alfaSource2], alfa.AuthorizedSources);
        Assert.DoesNotContain(BetaCompanyId.ToString(), alfa.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fonte-beta-1", alfa.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("da Beta", alfa.ToString(), StringComparison.Ordinal);

        Assert.Equal(AgentOutputGateStatus.Result, beta.Status);
        Assert.Equal("ok", beta.ReasonCode);
        Assert.Equal([betaSource], beta.AuthorizedSources);
        Assert.DoesNotContain(AlfaCompanyId.ToString(), beta.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fonte-alfa", beta.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("da Alfa", beta.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_version_is_present_on_every_outcome()
    {
        var gate = Gate();
        var outcomes = new[]
        {
            // ok
            gate.Evaluate(new AgentOutputGateRequest(
                Context(),
                Response("Resumo simples", [new AgentCitedSource("fonte-alfa", AlfaCompanyId)], 0.9),
                Contract(),
                SourcesRequired: false)),
            // cross_tenant_source
            gate.Evaluate(new AgentOutputGateRequest(
                Context(),
                Response("Resumo com fonte estranha", [new AgentCitedSource("fonte-beta", BetaCompanyId)], 0.9),
                Contract(),
                SourcesRequired: true)),
            // no_authorized_source
            gate.Evaluate(new AgentOutputGateRequest(
                Context(),
                Response("Resumo sem fontes", [], 0.9),
                Contract(),
                SourcesRequired: true)),
            // low_confidence
            gate.Evaluate(new AgentOutputGateRequest(
                Context(),
                Response("Resumo incerto", [new AgentCitedSource("fonte-alfa", AlfaCompanyId)], 0.2),
                Contract(),
                SourcesRequired: false)),
            // schema_invalid
            gate.Evaluate(new AgentOutputGateRequest(
                Context(),
                Response("Resumo quebrado", [], 0.9, structuredJson: "{ invalido"),
                Contract(),
                SourcesRequired: false)),
            // no_evidence
            gate.Evaluate(new AgentOutputGateRequest(
                Context(),
                Response(null, [], 0.9),
                Contract(),
                SourcesRequired: false)),
        };

        foreach (var result in outcomes)
        {
            Assert.Equal(AgentPolicyVersion.Current, result.PolicyVersion);
            Assert.False(string.IsNullOrEmpty(result.PolicyVersion));
        }
    }

    private static AgentOutputGate Gate() => new(new SourceAuthorizationGate());

    private static AgentModelResponse Response(
        string? text,
        IReadOnlyList<AgentCitedSource> sources,
        double? confidence,
        bool hasToolProposal = false,
        string? structuredJson = null) => new(
        Text: text,
        StructuredJson: structuredJson ?? """{ "titulo": "R", "corpo": "C" }""",
        CitedSources: sources,
        Confidence: confidence,
        HasToolProposal: hasToolProposal);

    private static AgentExecutionContext Context(Guid? companyId = null, string? companyName = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        companyId ?? AlfaCompanyId,
        companyName ?? "Alfa",
        Guid.NewGuid(),
        "resumo-diario",
        new HashSet<string>());

    private static AgentOutputContract Contract() => new("resumo-diario", new HashSet<string> { "titulo", "corpo" });
}
