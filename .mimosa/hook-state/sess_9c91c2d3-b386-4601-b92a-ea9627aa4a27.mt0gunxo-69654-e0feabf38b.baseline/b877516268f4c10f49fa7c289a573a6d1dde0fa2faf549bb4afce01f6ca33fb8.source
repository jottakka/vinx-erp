using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

public sealed class AgentOutputGateGroundingTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid BetaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    [Fact]
    public void Grounded_response_returns_result_with_own_company_sources_only()
    {
        var gate = Gate();
        var alfaSource = new AgentCitedSource("fonte-alfa", AlfaCompanyId);
        var response = Response(text: "Resumo fundamentado nas notas do dia", sources: [alfaSource], confidence: 0.9);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal("ok", result.ReasonCode);
        Assert.Equal(AgentPolicyVersion.Current, result.PolicyVersion);
        Assert.NotNull(result.RedactedText);

        // Somente a fonte da empresa do contexto chega ao canal.
        var source = Assert.Single(result.AuthorizedSources);
        Assert.Equal(alfaSource, source);
        Assert.DoesNotContain(BetaCompanyId.ToString(), result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_tenant_source_denies_without_exposing_intruder_anywhere()
    {
        var gate = Gate();
        var response = Response(
            text: "Resumo com fonte estranha",
            sources:
            [
                new AgentCitedSource("fonte-alfa", AlfaCompanyId),
                new AgentCitedSource("fonte-beta", BetaCompanyId),
            ],
            confidence: 0.9);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        Assert.Equal(AgentOutputGateStatus.Denied, result.Status);
        Assert.Equal("cross_tenant_source", result.ReasonCode);
        Assert.Null(result.RedactedText);
        Assert.Empty(result.AuthorizedSources);
        Assert.Equal(AgentPolicyVersion.Current, result.PolicyVersion);

        // A fonte intrusa não aparece em nenhum campo do resultado, nem texto vaza.
        Assert.DoesNotContain(BetaCompanyId.ToString(), result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fonte-beta", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("estranha", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sources_required_without_authorized_source_abstains()
    {
        var gate = Gate();
        var response = Response(text: "Resumo sem fontes", sources: [], confidence: 0.9);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        Assert.Equal(AgentOutputGateStatus.NeedsClarification, result.Status);
        Assert.Equal("no_authorized_source", result.ReasonCode);

        // Abstenção: o texto nunca chega ao canal como fato.
        Assert.Null(result.RedactedText);
        Assert.Empty(result.AuthorizedSources);
        Assert.DoesNotContain("sem fontes", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Low_confidence_abstains()
    {
        var gate = Gate();
        var response = Response(
            text: "Resumo incerto",
            sources: [new AgentCitedSource("fonte-alfa", AlfaCompanyId)],
            confidence: 0.2);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        Assert.Equal(AgentOutputGateStatus.NeedsClarification, result.Status);
        Assert.Equal("low_confidence", result.ReasonCode);
        Assert.Equal(0.5, AgentOutputGate.MinimumConfidence);
        Assert.Null(result.RedactedText);
        Assert.Empty(result.AuthorizedSources);
        Assert.DoesNotContain("incerto", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_text_without_sources_abstains_with_no_evidence()
    {
        var gate = Gate();
        var response = Response(text: null, sources: [], confidence: 0.9);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: false));

        Assert.Equal(AgentOutputGateStatus.NeedsClarification, result.Status);
        Assert.Equal("no_evidence", result.ReasonCode);
        Assert.Null(result.RedactedText);
        Assert.Empty(result.AuthorizedSources);
    }

    [Fact]
    public void Null_confidence_does_not_abstain()
    {
        var gate = Gate();
        var response = Response(
            text: "Resumo sem confiança reportada",
            sources: [new AgentCitedSource("fonte-alfa", AlfaCompanyId)],
            confidence: null);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        // Sem confiança reportada não há base para abster: a resposta fundamentada passa.
        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal("ok", result.ReasonCode);
        Assert.NotNull(result.RedactedText);
        Assert.Single(result.AuthorizedSources);
    }

    private static AgentOutputGate Gate() =>
        new(new SourceAuthorizationGate(), new SensitiveOutputRedactor(new HashSet<string>()));

    private static AgentModelResponse Response(string? text, IReadOnlyList<AgentCitedSource> sources, double? confidence) => new(
        Text: text,
        StructuredJson: """{ "titulo": "R", "corpo": "C" }""",
        CitedSources: sources,
        Confidence: confidence,
        HasToolProposal: false);

    private static AgentExecutionContext Context() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        AlfaCompanyId,
        "Alfa",
        Guid.NewGuid(),
        "resumo-diario",
        new HashSet<string>());

    private static AgentOutputContract Contract() => new("resumo-diario", new HashSet<string> { "titulo", "corpo" });
}
