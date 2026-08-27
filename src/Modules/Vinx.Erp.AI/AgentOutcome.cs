using Vinx.Erp.AI.Guardrails;

namespace Vinx.Erp.AI;

/// <summary>
/// Desfecho normalizado da execução do agente: único contrato que sai do
/// runner, cobrindo sucesso, interação pendente e as falhas fail-closed de
/// validação, acesso, contexto e modelo.
/// </summary>
public enum AgentOutcomeStatus
{
    Completed,
    NeedsClarification,
    RequiresConfirmation,
    Denied,
    Invalid,
    Unavailable,
    TimedOut,
}

/// <summary>
/// Reason codes estáveis de todos os desfechos do agente: unifica os códigos
/// de resolução de contexto (Tasks 2-3) e os do runner. O runner copia o
/// reason recebido da resolução sem revalidar semântica.
/// </summary>
public static class AgentOutcomeReason
{
    public const string Completed = "completed";
    public const string MessageEmpty = "message_empty";
    public const string ProfileUnknown = "profile_unknown";
    public const string AttachmentsNotAllowed = "attachments_not_allowed";
    public const string DocumentContextUnavailable = "document_context_unavailable";
    public const string CompanyRequired = "company_required";
    public const string CompanyNotAuthorized = "company_not_authorized";
    public const string CapabilityDenied = "capability_denied";
    public const string ContextUnavailable = "context_unavailable";
    public const string ProviderNotConfigured = "provider_not_configured";
    public const string ModelUnavailable = "model_unavailable";
    public const string ModelTimeout = "model_timeout";
    public const string ToolProposalRequiresConfirmation = "tool_proposal_requires_confirmation";
}

/// <summary>
/// Resultado seguro da execução: texto da resposta, proposta Pagaí tipada ou
/// proposta de ação a confirmar; nunca prompt, contexto, JSON/OCR bruto, JWT,
/// PII ou segredo. Tool call não executa no runner.
/// </summary>
public sealed record AgentOutcome(
    AgentOutcomeStatus Status,
    string ReasonCode,
    string? Text,
    string? ProposedActionName,
    Guid CorrelationId,
    TimeSpan Elapsed,
    PaguaiDocumentProposal? PaguaiProposal = null);
