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
/// Permissão de entrega de aprovação: infalsificável fora do assembly (sem
/// construtor público), vinculada a proposta + <c>CallId</c> e de uso único.
/// Criada exclusivamente pelo coordenador após claim durável e consumida pela
/// costura de entrega do gate.
/// </summary>
public sealed class HarnessApprovalDeliveryPermit
{
    private readonly Guid proposalId;
    private readonly string callId;
    private int consumed;

    internal HarnessApprovalDeliveryPermit(Guid proposalId, string callId)
    {
        if (proposalId == Guid.Empty)
        {
            throw new ArgumentException("O proposalId da permissão não pode ser vazio.", nameof(proposalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        this.proposalId = proposalId;
        this.callId = callId;
    }

    internal void Consume(Guid proposalId, string callId)
    {
        if (proposalId != this.proposalId || !string.Equals(callId, this.callId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Permissão de entrega não corresponde à proposta/solicitação pendente (fail-closed).");
        }

        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
        {
            throw new InvalidOperationException("Permissão de entrega já consumida (uso único).");
        }
    }
}

/// <summary>
/// Fronteira de aprovação do VINX (código do servidor). Consome
/// <see cref="ToolApprovalRequestContent"/> emitido pelo harness e decide com
/// contexto fornecido pelo servidor, allowlist e classificação de risco.
/// Aprovações duráveis, auditoria e idempotência vivem na Foundation e são
/// orquestradas por <see cref="HarnessConfirmationCoordinator"/>; o gate nunca
/// retoma uma sessão por conta própria nem promete execução exatamente-uma-vez.
/// Toda entrega real de resposta de aprovação passa pela costura interna
/// <c>DeliverApprovalResponseAsync</c>, que revalida contexto, allowlist de
/// escritas, forma exata da mensagem, correspondência de <c>CallId</c>, decisão
/// esperada e consumo da permissão antes de qualquer <c>RunAsync</c>.
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
    /// Confirma explicitamente a proposta (decisão do servidor, nunca do modelo).
    /// Exige permissão de entrega vinculada à proposta (criada pelo coordenador
    /// após claim durável); a própria entrega revalida o gate na costura interna.
    /// </summary>
    public Task<AgentResponse> ApproveAsync(
        AIAgent agent,
        AgentSession session,
        VinxApprovalDecision decision,
        Guid proposalId,
        HarnessApprovalDeliveryPermit permit,
        VinxExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateApproval(decision, context);
        return DeliverApprovalResponseAsync(
            agent,
            session,
            new ChatMessage(ChatRole.User, [decision.Request!.CreateResponse(true)]),
            decision.Request!,
            expectedApproved: true,
            proposalId,
            permit,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Confirma explicitamente a proposta a partir da solicitação pendente,
    /// com permissão de entrega e revalidação completa na costura interna.
    /// </summary>
    public Task<AgentResponse> ApproveAsync(
        AIAgent agent,
        AgentSession session,
        ToolApprovalRequestContent request,
        Guid proposalId,
        HarnessApprovalDeliveryPermit permit,
        VinxExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DeliverApprovalResponseAsync(
            agent,
            session,
            new ChatMessage(ChatRole.User, [request.CreateResponse(true)]),
            request,
            expectedApproved: true,
            proposalId,
            permit,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Nega a proposta e devolve o veto ao agente na mesma sessão. Interno:
    /// somente o coordenador entrega veto, após veto durável na Foundation.
    /// </summary>
    internal Task<AgentResponse> DenyAsync(
        AIAgent agent,
        AgentSession session,
        ToolApprovalRequestContent request,
        Guid proposalId,
        VinxExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DeliverApprovalResponseAsync(
            agent,
            session,
            new ChatMessage(ChatRole.User, [request.CreateResponse(false, "negado pela política VINX")]),
            request,
            expectedApproved: false,
            proposalId,
            permit: null,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Costura única de entrega de resposta de aprovação (fail-closed). Ordem
    /// de validação antes de qualquer <c>RunAsync</c>: contexto válido;
    /// <c>CallId</c>/nome derivados da solicitação pendente; classificação de
    /// escrita na allowlist; mensagem com EXATAMENTE um conteúdo, sendo
    /// <see cref="ToolApprovalResponseContent"/>; <c>CallId</c> correspondente;
    /// decisão esperada; aprovação consome permissão vinculada (proposta +
    /// <c>CallId</c>), veto exige permissão ausente.
    /// </summary>
    internal async Task<AgentResponse> DeliverApprovalResponseAsync(
        AIAgent agent,
        AgentSession session,
        ChatMessage message,
        ToolApprovalRequestContent pendingRequest,
        bool expectedApproved,
        Guid proposalId,
        HarnessApprovalDeliveryPermit? permit,
        VinxExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(pendingRequest);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsValid)
        {
            throw new InvalidOperationException("Aprovação recusada pelo gate VINX: contexto confiável ausente.");
        }

        var pendingCall = pendingRequest.ToolCall as FunctionCallContent;
        var pendingCallId = pendingCall?.CallId;
        var toolName = pendingCall?.Name;
        if (string.IsNullOrWhiteSpace(pendingCallId) || string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException(
                "Aprovação recusada pelo gate VINX: solicitação pendente sem chamada nomeada.");
        }

        if (!VinxToolPolicy.TryClassify(toolName, out var risk) || risk != VinxToolRisk.Write)
        {
            LogDecision(toolName, VinxApprovalOutcome.Denied, "ferramenta fora da allowlist de escritas");
            throw new InvalidOperationException(
                "Aprovação recusada pelo gate VINX: ferramenta fora da allowlist de escritas.");
        }

        if (message.Contents.Count != 1 || message.Contents[0] is not ToolApprovalResponseContent response)
        {
            throw new InvalidOperationException(
                "Entrega de aprovação deve conter exatamente uma resposta de aprovação (fail-closed).");
        }

        var responseCallId = (response.ToolCall as FunctionCallContent)?.CallId;
        if (!string.Equals(responseCallId, pendingCallId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resposta de aprovação sem correspondência com a solicitação pendente (fail-closed).");
        }

        if (response.Approved != expectedApproved)
        {
            throw new InvalidOperationException(
                "Decisão da resposta de aprovação diverge da decisão do servidor (fail-closed).");
        }

        if (expectedApproved)
        {
            if (permit is null)
            {
                throw new InvalidOperationException(
                    "Entrega de aprovação exige permissão vinculada à proposta (fail-closed).");
            }

            permit.Consume(proposalId, pendingCallId);
        }
        else if (permit is not null)
        {
            throw new InvalidOperationException(
                "Entrega de veto não admite permissão de aprovação (fail-closed).");
        }

        LogDecision(
            toolName,
            expectedApproved ? VinxApprovalOutcome.PendingConfirmation : VinxApprovalOutcome.Denied,
            expectedApproved ? "aprovação concedida pelo servidor" : "veto do servidor");
        return await agent.RunAsync(message, session, cancellationToken: cancellationToken).ConfigureAwait(false);
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
