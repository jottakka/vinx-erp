using Microsoft.AspNetCore.Http;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.Agent;

/// <summary>Resultado HTTP de uma execução do agente.</summary>
public sealed record AgentRunResult(AgentRunHttpResponse? Response, string? ErrorCode, int? HttpStatusCode)
{
    public static AgentRunResult Success(AgentRunHttpResponse response, int httpStatusCode) =>
        new(response, null, httpStatusCode);

    public static AgentRunResult Failure(string errorCode, int httpStatusCode) =>
        new(null, errorCode, httpStatusCode);
}

/// <summary>
/// Adaptador fino da fronteira HTTP: converte o contrato web para o comando
/// canônico e mapeia a disposição segura do serviço compartilhado.
/// </summary>
public sealed class AgentExecutionApplicationService(AgentExecutionService executionService)
{
    public async Task<AgentRunResult> RunAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await executionService.ExecuteAsync(
            new AgentCommand(
                actor,
                Channel: "web",
                InboundMessageId: null,
                request.RequestId,
                request.Journey,
                request.Message,
                request.CompanyId,
                request.Attachments),
            cancellationToken).ConfigureAwait(false);

        return result.Disposition switch
        {
            AgentExecutionDisposition.Completed => MapOutcome(result.Outcome!),
            AgentExecutionDisposition.Conflict => AgentRunResult.Success(
                new AgentRunHttpResponse(
                    AgentRunStatus.Conflict,
                    result.ReasonCode,
                    Text: null,
                    Sources: null,
                    actor.CorrelationId),
                StatusCodes.Status409Conflict),
            AgentExecutionDisposition.InProgress => AgentRunResult.Failure(
                "execution_in_progress", StatusCodes.Status409Conflict),
            AgentExecutionDisposition.Ambiguous when result.Outcome?.Status == AgentOutcomeStatus.TimedOut =>
                MapOutcome(result.Outcome),
            AgentExecutionDisposition.Ambiguous when result.ReasonCode == AgentOutcomeReason.ModelTimeout =>
                AgentRunResult.Failure("agent_timeout", StatusCodes.Status504GatewayTimeout),
            AgentExecutionDisposition.Ambiguous => AgentRunResult.Failure(
                "execution_ambiguous", StatusCodes.Status503ServiceUnavailable),
            AgentExecutionDisposition.FailedClosed => AgentRunResult.Failure(
                string.Equals(result.ReasonCode, AgentOutcomeReason.ProviderNotConfigured, StringComparison.Ordinal)
                    ? AgentOutcomeReason.ProviderNotConfigured
                    : "agent_unavailable",
                StatusCodes.Status503ServiceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Disposition, "Unsupported execution disposition."),
        };
    }

    /// <summary>Mapeamento puro do desfecho do runner para a resposta HTTP.</summary>
    public static AgentRunResult MapOutcome(AgentOutcome outcome) =>
        outcome.Status switch
        {
            AgentOutcomeStatus.Completed => Typed(AgentRunStatus.Result, outcome, StatusCodes.Status200OK),
            AgentOutcomeStatus.NeedsClarification => Typed(AgentRunStatus.NeedsClarification, outcome, StatusCodes.Status200OK),
            AgentOutcomeStatus.Denied => Typed(AgentRunStatus.Denied, outcome, StatusCodes.Status200OK),
            AgentOutcomeStatus.RequiresConfirmation => AgentRunResult.Success(
                new AgentRunHttpResponse(
                    AgentRunStatus.RequiresConfirmation,
                    outcome.ReasonCode,
                    Text: null,
                    Sources: null,
                    outcome.CorrelationId),
                StatusCodes.Status200OK),
            AgentOutcomeStatus.Invalid => Typed(AgentRunStatus.ValidationError, outcome, StatusCodes.Status400BadRequest),
            AgentOutcomeStatus.Unavailable => AgentRunResult.Failure(
                string.Equals(outcome.ReasonCode, AgentOutcomeReason.ProviderNotConfigured, StringComparison.Ordinal)
                    ? AgentOutcomeReason.ProviderNotConfigured
                    : "agent_unavailable",
                StatusCodes.Status503ServiceUnavailable),
            AgentOutcomeStatus.TimedOut => AgentRunResult.Failure("agent_timeout", StatusCodes.Status504GatewayTimeout),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Status, "Unsupported agent outcome status."),
        };

    private static AgentRunResult Typed(string status, AgentOutcome outcome, int httpStatusCode) =>
        AgentRunResult.Success(
            new AgentRunHttpResponse(status, outcome.ReasonCode, outcome.Text, Sources: null, outcome.CorrelationId),
            httpStatusCode);
}
