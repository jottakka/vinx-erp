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

    /// <summary>Proposta negada (contexto ausente, ferramenta fora da allowlist ou sem nome).</summary>
    Denied,
}

/// <summary>Decisão do gate sobre uma proposta de ferramenta.</summary>
public sealed record VinxApprovalDecision(
    VinxApprovalOutcome Outcome,
    ToolApprovalRequestContent? Request,
    VinxToolRisk? Risk,
    string? ToolName,
    string? Reason);

/// <summary>
/// Fronteira de aprovação do VINX (código do servidor). Consome
/// <see cref="ToolApprovalRequestContent"/> emitido pelo harness e decide com
/// contexto fornecido pelo servidor, allowlist e classificação de risco.
/// Aprovações duráveis, auditoria e idempotência chegam no PR 2; nada aqui
/// promete execução exatamente-uma-vez.
/// </summary>
public sealed class HarnessApprovalGate
{
    private const string DecisionLogTemplate =
        "Decisão de aprovação de ferramenta do harness. Módulo: {Module}, Ferramenta: {ToolName}, Decisão: {Decision}, Motivo: {Reason}";

    private readonly ILogger<HarnessApprovalGate> logger;

    public HarnessApprovalGate(ILogger<HarnessApprovalGate> logger) => this.logger = logger;

    /// <summary>
    /// Avalia a resposta do agente. O contexto confiável vem exclusivamente do
    /// servidor; valores de ator/empresa presentes nos argumentos do modelo são
    /// ignorados. Falta de contexto ou ferramenta fora da allowlist nega.
    /// </summary>
    public VinxApprovalDecision Evaluate(AgentResponse response, VinxExecutionContext? context)
    {
        var request = FindApprovalRequest(response);
        if (request is null)
        {
            return new VinxApprovalDecision(VinxApprovalOutcome.None, null, null, null, null);
        }

        var toolName = GetToolName(request);

        if (context is null || !context.IsValid)
        {
            LogDecision(toolName, VinxApprovalOutcome.Denied, "contexto confiável ausente");
            return new VinxApprovalDecision(
                VinxApprovalOutcome.Denied, request, null, toolName, "contexto confiável ausente");
        }

        if (!VinxToolPolicy.TryClassify(toolName, out var risk))
        {
            LogDecision(toolName, VinxApprovalOutcome.Denied, "ferramenta fora da allowlist");
            return new VinxApprovalDecision(
                VinxApprovalOutcome.Denied, request, null, toolName, "ferramenta fora da allowlist");
        }

        if (risk == VinxToolRisk.Write)
        {
            LogDecision(toolName, VinxApprovalOutcome.PendingConfirmation, "escrita exige confirmação explícita");
            return new VinxApprovalDecision(
                VinxApprovalOutcome.PendingConfirmation, request, risk, toolName, "escrita exige confirmação explícita");
        }

        LogDecision(toolName, VinxApprovalOutcome.PendingConfirmation, "leitura permitida pela allowlist");
        return new VinxApprovalDecision(
            VinxApprovalOutcome.PendingConfirmation, request, risk, toolName, "leitura permitida pela allowlist");
    }

    /// <summary>
    /// Confirma explicitamente a proposta (decisão do servidor, nunca do modelo)
    /// e retoma o agente na mesma sessão.
    /// </summary>
    public Task<AgentResponse> ApproveAsync(
        AIAgent agent,
        AgentSession session,
        ToolApprovalRequestContent request,
        VinxExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        LogDecision(GetToolName(request), VinxApprovalOutcome.PendingConfirmation, "aprovação concedida pelo servidor");
        var approvalMessage = new ChatMessage(ChatRole.User, [request.CreateResponse(true)]);
        return agent.RunAsync(approvalMessage, session, cancellationToken: cancellationToken);
    }

    /// <summary>Nega a proposta e devolve o veto ao agente na mesma sessão.</summary>
    public Task<AgentResponse> DenyAsync(
        AIAgent agent,
        AgentSession session,
        ToolApprovalRequestContent request,
        VinxExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        LogDecision(GetToolName(request), VinxApprovalOutcome.Denied, "veto do servidor");
        var vetoMessage = new ChatMessage(ChatRole.User, [request.CreateResponse(false, "negado pela política VINX")]);
        return agent.RunAsync(vetoMessage, session, cancellationToken: cancellationToken);
    }

    private static string? GetToolName(ToolApprovalRequestContent request) =>
        (request.ToolCall as FunctionCallContent)?.Name;

    private static ToolApprovalRequestContent? FindApprovalRequest(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent request)
                {
                    return request;
                }
            }
        }

        return null;
    }

    private void LogDecision(string? toolName, VinxApprovalOutcome outcome, string reason)
    {
        var level = outcome == VinxApprovalOutcome.Denied ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, DecisionLogTemplate, "AI", toolName ?? "(sem nome)", outcome, reason);
    }
}
