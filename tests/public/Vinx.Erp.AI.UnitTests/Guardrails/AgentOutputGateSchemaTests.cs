using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

public sealed class AgentOutputGateSchemaTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid BetaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    [Fact]
    public void Valid_structured_response_returns_result_with_authorized_sources()
    {
        var gate = new AgentOutputGate(new SourceAuthorizationGate(), new SensitiveOutputRedactor());
        var authorizedSource = new AgentCitedSource("fonte-1", AlfaCompanyId);
        var response = new AgentModelResponse(
            Text: "Resumo enviado para contato@exemplo.com",
            StructuredJson: """{ "titulo": "R", "corpo": "C" }""",
            CitedSources: [authorizedSource],
            Confidence: 0.9,
            HasToolProposal: false);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: false));

        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal("ok", result.ReasonCode);
        Assert.Equal(AgentPolicyVersion.Current, result.PolicyVersion);
        Assert.Equal("2026-08-19.1", result.PolicyVersion);

        // A fonte da empresa do contexto chega ao canal; a de outra empresa agora nega (fatia de grounding).
        var source = Assert.Single(result.AuthorizedSources);
        Assert.Equal(authorizedSource, source);
        Assert.DoesNotContain(BetaCompanyId.ToString(), result.ToString(), StringComparison.Ordinal);

        // O texto liberado já passa pelo redator: e-mail vira marcador fixo.
        Assert.NotNull(result.RedactedText);
        Assert.Contains("[email-redigido]", result.RedactedText!);
        Assert.DoesNotContain("exemplo.com", result.RedactedText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_schema_blocks_text_from_channel()
    {
        var gate = new AgentOutputGate(new SourceAuthorizationGate(), new SensitiveOutputRedactor());
        var response = new AgentModelResponse(
            Text: "resumo com dado sensivel",
            StructuredJson: "{ invalido",
            CitedSources: [new AgentCitedSource("fonte-1", AlfaCompanyId)],
            Confidence: 0.9,
            HasToolProposal: false);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: false));

        Assert.Equal(AgentOutputGateStatus.ValidationError, result.Status);
        Assert.Equal("schema_invalid", result.ReasonCode);
        Assert.Null(result.RedactedText);
        Assert.Empty(result.AuthorizedSources);
        Assert.Equal(AgentPolicyVersion.Current, result.PolicyVersion);

        // Fail-closed: nem o texto nem a fonte chegam ao canal em nenhum campo do resultado.
        Assert.DoesNotContain("sensivel", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fonte-1", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_proposal_flag_is_preserved_regardless_of_status()
    {
        var gate = new AgentOutputGate(new SourceAuthorizationGate(), new SensitiveOutputRedactor());
        // Fonte autorizada presente para não cair em no_evidence (regra de fatia posterior).
        var accepted = new AgentModelResponse(
            Text: null,
            StructuredJson: """{ "titulo": "R", "corpo": "C" }""",
            CitedSources: [new AgentCitedSource("fonte-1", AlfaCompanyId)],
            Confidence: null,
            HasToolProposal: true);
        var rejected = new AgentModelResponse(
            Text: null,
            StructuredJson: "quebrado",
            CitedSources: [],
            Confidence: null,
            HasToolProposal: true);

        var validResult = gate.Evaluate(new AgentOutputGateRequest(Context(), accepted, Contract(), SourcesRequired: false));
        var invalidResult = gate.Evaluate(new AgentOutputGateRequest(Context(), rejected, Contract(), SourcesRequired: false));

        Assert.Equal(AgentOutputGateStatus.Result, validResult.Status);
        Assert.True(validResult.HasToolProposal);
        Assert.Equal(AgentOutputGateStatus.ValidationError, invalidResult.Status);
        Assert.True(invalidResult.HasToolProposal);
    }

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
