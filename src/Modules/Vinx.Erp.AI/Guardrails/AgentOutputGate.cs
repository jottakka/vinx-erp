namespace Vinx.Erp.AI.Guardrails;

/// <summary>
/// Desfecho do gate de saída: texto liberado, esclarecimento pedido, negação
/// ou falha de validação do contrato.
/// </summary>
public enum AgentOutputGateStatus
{
    Result,
    NeedsClarification,
    Denied,
    ValidationError,
}

/// <summary>
/// Resultado do gate: texto já redigido (null quando nada chega ao canal),
/// fontes autorizadas, reason code, versão da política aplicada e a proposta
/// de tool sempre espelhada da resposta.
/// </summary>
public sealed record AgentOutputGateResult(
    AgentOutputGateStatus Status,
    string ReasonCode,
    string? RedactedText,
    IReadOnlyList<AgentCitedSource> AuthorizedSources,
    string PolicyVersion,
    bool HasToolProposal);

/// <summary>
/// Entrada do gate: contexto resolvido, resposta normalizada do modelo e
/// contrato da jornada; SourcesRequired exige ao menos uma fonte autorizada
/// (aplicado junto às regras de evidência).
/// </summary>
public sealed record AgentOutputGateRequest(
    AgentExecutionContext Context,
    AgentModelResponse Response,
    AgentOutputContract Contract,
    bool SourcesRequired);

/// <summary>
/// Gate determinístico de saída com ordem fixa: primeiro o contrato
/// estruturado — schema inválido reprova sem expor texto —, depois a
/// autorização de fontes e a redação do texto liberado. As demais regras da
/// política (injeção, confiança, evidência) entram nas próximas fatias
/// mantendo esta ordem.
/// </summary>
public sealed class AgentOutputGate(SourceAuthorizationGate sources, SensitiveOutputRedactor redactor)
{
    /// <summary>Reason codes estáveis das decisões do gate.</summary>
    public const string Ok = "ok";
    public const string SchemaInvalid = "schema_invalid";
    public const string RequiresConfirmationState = "requires_confirmation_state";
    public const string PromptInjectionDetected = "prompt_injection_detected";
    public const string CrossTenantSource = "cross_tenant_source";
    public const string NoAuthorizedSource = "no_authorized_source";
    public const string LowConfidence = "low_confidence";
    public const string NoEvidence = "no_evidence";

    /// <summary>Avalia a resposta do modelo contra a política de saída vigente.</summary>
    public AgentOutputGateResult Evaluate(AgentOutputGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = request.Contract.Validate(request.Response.StructuredJson);
        if (!validation.IsValid)
        {
            // Fail-closed: nem texto redigido nem fontes chegam ao canal; só a proposta segue preservada.
            return new AgentOutputGateResult(
                AgentOutputGateStatus.ValidationError,
                SchemaInvalid,
                RedactedText: null,
                AuthorizedSources: [],
                AgentPolicyVersion.Current,
                request.Response.HasToolProposal);
        }

        return new AgentOutputGateResult(
            AgentOutputGateStatus.Result,
            Ok,
            redactor.Redact(request.Response.Text),
            sources.Authorize(request.Context, request.Response.CitedSources),
            AgentPolicyVersion.Current,
            request.Response.HasToolProposal);
    }
}
