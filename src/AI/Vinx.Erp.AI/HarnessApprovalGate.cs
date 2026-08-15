using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Vinx.Erp.AI;

/// <summary>Resultado da avaliação de uma proposta de ferramenta pelo gate VINX.</summary>
public enum VinxApprovalOutcome
{
    /// <summary>Nenhuma proposta de aprovação pendente na resposta.</summary>
    None,

    /// <summary>Proposta válida; aguarda confirmação explícita do servidor.</summary>
    PendingConfirmation,

    /// <summary>Proposta negada por contexto ou política.</summary>
    Denied,
}

/// <summary>Decisão do gate sobre uma proposta de ferramenta.</summary>
public sealed record VinxApprovalDecision(
    VinxApprovalOutcome Outcome,
    ToolApprovalRequestContent? Request,
    string? ToolName,
    string? Reason);

/// <summary>
/// Classifica propostas do framework usando somente contexto confiável e a
/// allowlist do servidor. Não entrega respostas de aprovação nem retoma o
/// agente; essa capacidade permanece ausente até existir um caso de uso
/// hospedado.
/// </summary>
public sealed class HarnessApprovalGate
{
    private const string DecisionLogTemplate =
        "Decisão de aprovação de ferramenta do harness. Módulo: {Module}, Ferramenta: {ToolName}, Decisão: {Decision}, Motivo: {Reason}";

    private readonly ILogger<HarnessApprovalGate> logger;

    public HarnessApprovalGate(ILogger<HarnessApprovalGate> logger) =>
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Avalia no máximo uma solicitação pendente. <c>CallId</c> ausente,
    /// contexto inválido, ferramenta desconhecida, leitura protegida ou
    /// múltiplas solicitações são negados de forma fail-closed.
    /// </summary>
    public VinxApprovalDecision Evaluate(AgentResponse response, VinxExecutionContext? context)
    {
        ArgumentNullException.ThrowIfNull(response);

        var requests = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .Take(2)
            .ToArray();

        if (requests.Length == 0)
        {
            return new VinxApprovalDecision(VinxApprovalOutcome.None, null, null, null);
        }

        if (requests.Length > 1)
        {
            return Deny(null, null, "múltiplas solicitações de aprovação na mesma resposta");
        }

        var request = requests[0];
        var toolCall = request.ToolCall as FunctionCallContent;
        var toolName = toolCall?.Name;
        var isKnownTool = VinxToolPolicy.TryClassify(toolName, out var risk);
        var safeToolName = isKnownTool ? toolName : null;

        if (string.IsNullOrWhiteSpace(toolCall?.CallId))
        {
            return Deny(request, safeToolName, "solicitação sem CallId válido");
        }

        if (context is null || !context.IsValid)
        {
            return Deny(request, safeToolName, "contexto confiável ausente");
        }

        if (!isKnownTool)
        {
            return Deny(request, null, "ferramenta fora da allowlist");
        }

        if (risk != VinxToolRisk.Write)
        {
            return Deny(request, toolName, "leitura não deveria exigir aprovação");
        }

        const string reason = "escrita exige confirmação explícita";
        LogDecision(toolName, VinxApprovalOutcome.PendingConfirmation, reason);
        return new VinxApprovalDecision(
            VinxApprovalOutcome.PendingConfirmation,
            request,
            toolName,
            reason);
    }

    private VinxApprovalDecision Deny(
        ToolApprovalRequestContent? request,
        string? toolName,
        string reason)
    {
        LogDecision(toolName, VinxApprovalOutcome.Denied, reason);
        return new VinxApprovalDecision(VinxApprovalOutcome.Denied, request, toolName, reason);
    }

    private void LogDecision(string? toolName, VinxApprovalOutcome outcome, string reason)
    {
        var level = outcome == VinxApprovalOutcome.Denied ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, DecisionLogTemplate, "AI", toolName ?? "(sem nome)", outcome, reason);
    }
}
