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
/// detecção de injeção de prompt (passo 2, antes das fontes), a autorização
/// de fontes (violação cross-tenant nega), o grounding (fonte exigida
/// ausente, sem evidência) e a confiança mínima, sempre por abstenção.
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

    /// <summary>Confiança mínima para liberar a resposta; abaixo disso o gate se abstém.</summary>
    public const double MinimumConfidence = 0.5;

    /// <summary>
    /// Lista FECHADA de marcadores de injeção de prompt (PT/EN) comparados por
    /// match ordinal ignore-case sobre o texto; termos novos só entram por
    /// mudança de política versionada. A heurística é determinística e uma
    /// defesa-em-profundidade: serve para NEGAR conteúdo suspeito, nunca para
    /// autorizar nada. Texto de documento ou usuário é lido aqui apenas como
    /// DADOS — o gate nunca promove esse texto a instrução e não possui
    /// caminho de execução.
    /// </summary>
    public static readonly IReadOnlyList<string> InjectionMarkers =
    [
        "ignore as instruções",
        "ignore todas as instruções",
        "ignore all instructions",
        "ignore previous instructions",
        "disregard previous instructions",
        "system prompt",
        "reveal your prompt",
        "mostre seu prompt",
        "mostre o prompt do sistema",
        "you are now",
        "a partir de agora você é",
        "a partir de agora voce é",
    ];

    /// <summary>Avalia a resposta do modelo contra a política de saída vigente.</summary>
    public AgentOutputGateResult Evaluate(AgentOutputGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = request.Contract.Validate(request.Response.StructuredJson);
        if (!validation.IsValid)
        {
            return Withheld(request, AgentOutputGateStatus.ValidationError, SchemaInvalid);
        }

        // Injeção de prompt (passo 2, antes das fontes): texto é lido só como
        // dados; marcador da lista fechada nega sem expor o texto em nenhum campo.
        if (ContainsInjectionMarker(request.Response.Text))
        {
            return Withheld(request, AgentOutputGateStatus.Denied, PromptInjectionDetected);
        }

        // Fonte de outra empresa nega sem expor a intrusa em nenhum campo.
        var authorization = sources.Authorize(request.Context, request.Response.CitedSources);
        if (authorization.CrossTenantViolation)
        {
            return Withheld(request, AgentOutputGateStatus.Denied, CrossTenantSource);
        }

        // Fontes exigidas sem nenhuma autorizada: abstenção, nunca completa lacunas.
        if (request.SourcesRequired && authorization.AuthorizedSources.Count == 0)
        {
            return Withheld(request, AgentOutputGateStatus.NeedsClarification, NoAuthorizedSource);
        }

        // Confiança abaixo do piso abstém; null não compara (sem confiança reportada não há base para abster).
        if (request.Response.Confidence < MinimumConfidence)
        {
            return Withheld(request, AgentOutputGateStatus.NeedsClarification, LowConfidence);
        }

        // Sem texto e sem fontes não há evidência nenhuma: abstenção.
        if (string.IsNullOrEmpty(request.Response.Text) && authorization.AuthorizedSources.Count == 0)
        {
            return Withheld(request, AgentOutputGateStatus.NeedsClarification, NoEvidence);
        }

        // Caminho ok: o texto exposto é SEMPRE o redigido; a redação nunca altera o status.
        var redaction = redactor.Redact(request.Response.Text);
        return new AgentOutputGateResult(
            AgentOutputGateStatus.Result,
            Ok,
            redaction.RedactedText,
            authorization.AuthorizedSources,
            AgentPolicyVersion.Current,
            request.Response.HasToolProposal);
    }

    private static bool ContainsInjectionMarker(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var marker in InjectionMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AgentOutputGateResult Withheld(
        AgentOutputGateRequest request,
        AgentOutputGateStatus status,
        string reasonCode) =>
        // Nada chega ao canal: nem texto redigido nem fontes; só a proposta segue espelhada.
        new(status, reasonCode, RedactedText: null, AuthorizedSources: [], AgentPolicyVersion.Current, request.Response.HasToolProposal);
}
