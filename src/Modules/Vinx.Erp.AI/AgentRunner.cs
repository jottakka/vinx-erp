using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>
/// Orquestra uma execução do agente na ordem fixa da spec: validação de
/// entrada, resolução de contexto confiável, seleção de provider e UMA chamada
/// de modelo no máximo, toda falha é fail-closed. Stateless: nenhum estado
/// sobrevive entre chamadas. Tool call volta como proposta que exige
/// confirmação — o runner não executa nada. Nunca loga prompt, resposta ou PII.
/// </summary>
public sealed class AgentRunner(
    IAgentContextResolver contextResolver,
    IAgentModel? model,
    ILogger<AgentRunner> logger,
    Func<string, AgentTaskProfile?>? profileCatalog = null)
{
    private const string Operation = "run";
    private const string Module = "Vinx.Erp.AI";
    private const string ExecutionLogTemplate = "Agent execution {Operation} {Module} {Result} {Reason} {ElapsedMs}";

    private readonly Func<string, AgentTaskProfile?> lookupProfile = profileCatalog ?? LookupInCatalog;

    public async Task<AgentOutcome> RunAsync(
        ActorContext actor,
        string profileId,
        string message,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. Validação antes de qualquer consulta ou modelo.
        var profile = lookupProfile(profileId);
        var invalidReason = AgentPolicy.ValidateRequest(message, profile);
        if (invalidReason is not null)
        {
            return Finish(AgentOutcomeStatus.Invalid, invalidReason, actor, stopwatch);
        }

        // 2. Contexto confiável resolvido ANTES do modelo; reason copiado.
        var resolution = await contextResolver.ResolveAsync(actor, profile!, requestedCompanyId, cancellationToken);
        if (resolution.Status != AgentContextResolutionStatus.Resolved)
        {
            return Finish(MapResolutionStatus(resolution.Status), resolution.ReasonCode, actor, stopwatch);
        }

        // 3. Provider não atribuído ou cliente ausente: fail-closed até #135.
        if (model is null || profile!.ModelSelection is null)
        {
            return Finish(AgentOutcomeStatus.Unavailable, AgentOutcomeReason.ProviderNotConfigured, actor, stopwatch);
        }

        // 4. Turn normalizado: uma única mensagem do usuário, uma chamada.
        var messages = new List<ChatMessage> { new(ChatRole.User, message) };
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(profile.Timeout);

        ChatResponse response;
        try
        {
            response = await model.GetResponseAsync(messages, profile.ModelSelection, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Finish(
                AgentOutcomeStatus.TimedOut,
                AgentOutcomeReason.ModelTimeout,
                actor,
                stopwatch,
                exception: exception);
        }
        catch (Exception exception)
        {
            return Finish(
                AgentOutcomeStatus.Unavailable,
                AgentOutcomeReason.ModelUnavailable,
                actor,
                stopwatch,
                exception: exception);
        }

        // 5. Normalização do outcome: tool call é proposta, nunca execução.
        var toolCall = response.Messages
            .SelectMany(messageContent => messageContent.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault();
        if (toolCall is not null)
        {
            return Finish(
                AgentOutcomeStatus.RequiresConfirmation,
                AgentOutcomeReason.ToolProposalRequiresConfirmation,
                actor,
                stopwatch,
                proposedActionName: toolCall.Name);
        }

        return Finish(AgentOutcomeStatus.Completed, AgentOutcomeReason.Completed, actor, stopwatch, text: response.Text);
    }

    private AgentOutcome Finish(
        AgentOutcomeStatus status,
        string reasonCode,
        ActorContext actor,
        Stopwatch stopwatch,
        string? text = null,
        string? proposedActionName = null,
        Exception? exception = null)
    {
        stopwatch.Stop();
        var outcome = new AgentOutcome(status, reasonCode, text, proposedActionName, actor.CorrelationId, stopwatch.Elapsed);
        AgentTelemetry.RecordExecution(status, reasonCode);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = actor.CorrelationId });
        LogOutcome(outcome, exception);
        return outcome;
    }

    private void LogOutcome(AgentOutcome outcome, Exception? exception)
    {
        var result = AgentTelemetry.ToResultTag(outcome.Status);
        var elapsedMs = (long)outcome.Elapsed.TotalMilliseconds;

        // Níveis: validação esperada usa Information (regra AGENTS); Warning
        // apenas para negação; Error preservando a exceção do modelo.
        switch (outcome.Status)
        {
            case AgentOutcomeStatus.Denied:
                logger.LogWarning(ExecutionLogTemplate, Operation, Module, result, outcome.ReasonCode, elapsedMs);
                break;
            case AgentOutcomeStatus.Unavailable:
            case AgentOutcomeStatus.TimedOut:
                logger.LogError(exception, ExecutionLogTemplate, Operation, Module, result, outcome.ReasonCode, elapsedMs);
                break;
            default:
                logger.LogInformation(ExecutionLogTemplate, Operation, Module, result, outcome.ReasonCode, elapsedMs);
                break;
        }
    }

    private static AgentOutcomeStatus MapResolutionStatus(AgentContextResolutionStatus status) => status switch
    {
        AgentContextResolutionStatus.NeedsClarification => AgentOutcomeStatus.NeedsClarification,
        AgentContextResolutionStatus.Denied => AgentOutcomeStatus.Denied,
        AgentContextResolutionStatus.Unavailable => AgentOutcomeStatus.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported context resolution status."),
    };

    private static AgentTaskProfile? LookupInCatalog(string profileId) =>
        AgentTaskProfileCatalog.TryGet(profileId, out var profile) ? profile : null;
}
