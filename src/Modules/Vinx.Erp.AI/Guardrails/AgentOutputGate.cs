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
/// Resultado do gate: texto liberado (null quando nada chega ao canal),
/// fontes autorizadas, reason code, versão da política aplicada e a proposta
/// de tool sempre espelhada da resposta.
/// </summary>
public sealed record AgentOutputGateResult(
    AgentOutputGateStatus Status,
    string ReasonCode,
    string? Text,
    IReadOnlyList<AgentCitedSource> AuthorizedSources,
    string PolicyVersion,
    bool HasToolProposal,
    PaguaiDocumentProposal? PaguaiProposal = null);

/// <summary>
/// Entrada do gate: contexto resolvido, resposta normalizada do modelo e
/// contrato da jornada; SourcesRequired exige ao menos uma fonte autorizada
/// (aplicado junto às regras de evidência).
/// </summary>
public sealed record AgentOutputGateRequest(
    AgentExecutionContext Context,
    AgentModelResponse Response,
    AgentOutputContract Contract,
    bool SourcesRequired,
    PaguaiDocumentProposalParseRequest? PaguaiDocumentProposalRequest = null);

/// <summary>
/// Gate determinístico de saída com ordem fixa: primeiro o contrato
/// estrutural — schema inválido reprova sem expor texto —, depois a
/// autorização de fontes (violação cross-tenant nega), o grounding (fonte
/// exigida ausente, sem evidência) e a confiança mínima, sempre por
/// abstenção. O texto do caminho ok atravessa sem transformação: o gate não
/// redige conteúdo nem detecta injeção de prompt — verificação por padrões
/// fixos ou lista fechada de termos numa única linguagem não é confiável, e
/// redação/triagem de injeção, quando um canal exigir, será feita por
/// detector não determinístico decidido por canal (#138+).
/// </summary>
public sealed class AgentOutputGate(SourceAuthorizationGate sources)
{
    /// <summary>Reason codes estáveis das decisões do gate.</summary>
    public const string Ok = "ok";
    public const string SchemaInvalid = "schema_invalid";
    public const string RequiresConfirmationState = "requires_confirmation_state";
    public const string CrossTenantSource = "cross_tenant_source";
    public const string NoAuthorizedSource = "no_authorized_source";
    public const string LowConfidence = "low_confidence";
    public const string NoEvidence = "no_evidence";

    /// <summary>Confiança mínima para liberar a resposta; abaixo disso o gate se abstém.</summary>
    public const double MinimumConfidence = 0.5;

    /// <summary>Avalia a resposta do modelo contra a política de saída vigente.</summary>
    public AgentOutputGateResult Evaluate(AgentOutputGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Contract.RequiresPaguaiDocumentProposal)
        {
            return EvaluatePaguaiDocumentProposal(request);
        }

        var validation = request.Contract.Validate(request.Response.StructuredJson);
        if (!validation.IsValid)
        {
            return Withheld(request, AgentOutputGateStatus.ValidationError, SchemaInvalid);
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

        // Caminho ok: o texto liberado é o do modelo, sem transformação.
        return new AgentOutputGateResult(
            AgentOutputGateStatus.Result,
            Ok,
            request.Response.Text,
            authorization.AuthorizedSources,
            AgentPolicyVersion.Current,
            request.Response.HasToolProposal);
    }

    private static AgentOutputGateResult EvaluatePaguaiDocumentProposal(AgentOutputGateRequest request)
    {
        if (request.PaguaiDocumentProposalRequest is null)
        {
            return Withheld(request, AgentOutputGateStatus.ValidationError, SchemaInvalid);
        }

        var parsed = PaguaiDocumentProposalParser.Parse(request.PaguaiDocumentProposalRequest with
        {
            StructuredJson = request.Response.StructuredJson ?? string.Empty,
        });

        return parsed.Status switch
        {
            PaguaiProposalParseStatus.Proposal => new AgentOutputGateResult(
                AgentOutputGateStatus.Result,
                Ok,
                Text: null,
                AuthorizedSources: [],
                AgentPolicyVersion.Current,
                request.Response.HasToolProposal,
                parsed.Proposal),
            PaguaiProposalParseStatus.NeedsClarification => Withheld(
                request,
                AgentOutputGateStatus.NeedsClarification,
                parsed.Reason ?? SchemaInvalid),
            PaguaiProposalParseStatus.Invalid => Withheld(
                request,
                AgentOutputGateStatus.ValidationError,
                SchemaInvalid),
            _ => throw new ArgumentOutOfRangeException(nameof(parsed.Status), parsed.Status, "Unsupported Paguai proposal status."),
        };
    }

    private static AgentOutputGateResult Withheld(
        AgentOutputGateRequest request,
        AgentOutputGateStatus status,
        string reasonCode) =>
        // Nada chega ao canal: nem texto nem fontes; só a proposta segue espelhada.
        new(status, reasonCode, Text: null, AuthorizedSources: [], AgentPolicyVersion.Current, request.Response.HasToolProposal);
}
