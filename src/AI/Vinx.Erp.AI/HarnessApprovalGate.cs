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
    /// Respostas com mais de uma solicitação de aprovação pendente são negadas
    /// (fail-closed), sem expor solicitação para aprovação. Solicitação de
    /// aprovação para leitura é tratada como erro de configuração e negada,
    /// pois leituras não são envolvidas em <see cref="ApprovalRequiredAIFunction"/>.
    /// </summary>
    public VinxApprovalDecision Evaluate(AgentResponse response, VinxExecutionContext? context)
    {
        var requestCount = CountApprovalRequests(response);
        if (requestCount == 0)
        {
            return new VinxApprovalDecision(VinxApprovalOutcome.None, null, null, null, null);
        }

        if (requestCount > 1)
        {
            LogDecision(null, VinxApprovalOutcome.Denied, "múltiplas solicitações de aprovação na mesma resposta");
            return new VinxApprovalDecision(
                VinxApprovalOutcome.Denied,
                null,
                null,
                null,
                "múltiplas solicitações de aprovação na mesma resposta");
        }

        var request = FindApprovalRequest(response)!;
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

        LogDecision(toolName, VinxApprovalOutcome.Denied, "leitura não deveria exigir aprovação");
        return new VinxApprovalDecision(
            VinxApprovalOutcome.Denied, request, risk, toolName, "leitura não deveria exigir aprovação");
    }

    /// <summary>
    /// Confirma explicitamente a proposta (decisão do servidor, nunca do modelo)
    /// e retoma o agente na mesma sessão. A própria chamada revalida o gate:
    /// somente uma decisão <see cref="VinxApprovalOutcome.PendingConfirmation"/>
    /// com solicitação presente, contexto válido e ferramenta classificada como
    /// escrita pela allowlist é executada; qualquer outra combinação lança
    /// <see cref="InvalidOperationException"/> e nada executa.
    /// A aprovação deve ser a interação imediatamente seguinte com a sessão:
    /// uma resposta de aprovação sem correspondência com a solicitação pendente
    /// é consumida como entrada comum e descarta a pendência (fail-closed,
    /// nunca executa); o PR 2 tornará esses no-ops silenciosos erros explícitos.
    /// </summary>
    public Task<AgentResponse> ApproveAsync(
        AIAgent agent,
        AgentSession session,
        VinxApprovalDecision decision,
        VinxExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ValidateApproval(decision, context);

        var request = decision.Request!;
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

    private void ValidateApproval(VinxApprovalDecision decision, VinxExecutionContext? context)
    {
        string? refusal = null;

        if (decision.Outcome != VinxApprovalOutcome.PendingConfirmation || decision.Request is null)
        {
            refusal = "decisão sem aprovação pendente válida";
        }
        else if (context is null || !context.IsValid)
        {
            refusal = "contexto confiável ausente";
        }
        else if (!VinxToolPolicy.TryClassify(GetToolName(decision.Request), out var risk) || risk != VinxToolRisk.Write)
        {
            // Classifica o nome derivado da própria solicitação, sem confiar
            // no campo relatado pela decisão.
            refusal = "ferramenta fora da allowlist de escritas";
        }

        if (refusal is not null)
        {
            LogDecision(decision.ToolName, VinxApprovalOutcome.Denied, refusal);
            throw new InvalidOperationException($"Aprovação recusada pelo gate VINX: {refusal}.");
        }
    }

    private static int CountApprovalRequests(AgentResponse response)
    {
        var count = 0;
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent)
                {
                    count++;
                }
            }
        }

        return count;
    }

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
