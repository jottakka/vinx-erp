using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

// Fixtures sintéticas: domínios sob .example e identificadores de exemplo;
// nenhum dado pessoal real.
public sealed class AgentOutputGateRedactionTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void Result_text_is_always_redacted_and_never_echoes_the_email()
    {
        var gate = Gate();
        var response = Response(text: "Resumo enviado para joao.silva@example.com ontem", confidence: 0.9);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal("ok", result.ReasonCode);
        Assert.NotNull(result.RedactedText);
        Assert.Contains("[email-redigido]", result.RedactedText!, StringComparison.Ordinal);
        Assert.DoesNotContain("joao.silva@example.com", result.RedactedText!, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", result.RedactedText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_never_changes_gate_status()
    {
        var gate = Gate();
        // Texto lotado de dados sensíveis: a redação é cosmética, o desfecho permanece Result/ok.
        var response = Response(
            text: "CPF 12345678901, CNPJ 12345678000195 e https://malicioso.example/x",
            confidence: 0.9);

        var result = gate.Evaluate(new AgentOutputGateRequest(Context(), response, Contract(), SourcesRequired: true));

        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal("ok", result.ReasonCode);
        Assert.Contains("[identificador-redigido]", result.RedactedText!, StringComparison.Ordinal);
        Assert.Contains("[link-removido]", result.RedactedText!, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(result.RedactedText!, "[identificador-redigido]"));
        Assert.DoesNotContain("12345678", result.RedactedText!, StringComparison.Ordinal);
    }

    private static AgentOutputGate Gate() =>
        new(new SourceAuthorizationGate(), new SensitiveOutputRedactor(new HashSet<string>()));

    private static AgentModelResponse Response(string text, double? confidence) => new(
        Text: text,
        StructuredJson: """{ "titulo": "R", "corpo": "C" }""",
        CitedSources: [new AgentCitedSource("fonte-alfa", AlfaCompanyId)],
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

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
