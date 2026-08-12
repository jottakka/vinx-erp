using Vinx.Erp.AI.Models;
using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.AgentHarness;

public sealed class AgentHarnessRunner(
    IAgentModel model,
    AgentToolRegistry toolRegistry,
    AgentRunPolicy policy,
    IAgentRunObserver observer)
{
    private readonly IAgentModel model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly AgentToolRegistry toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
    private readonly AgentRunPolicy policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly IAgentRunObserver observer = observer ?? throw new ArgumentNullException(nameof(observer));

    public async Task<AgentRunOutcome> RunAsync(
        AgentExecutionContext context,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = Guid.NewGuid();
        var priorResults = new List<AgentToolResult>();
        var allowedDescriptors = toolRegistry.Descriptors
            .Where(descriptor => context.Profile.AllowedTools.Contains(descriptor.Name))
            .ToArray();
        var toolTurns = 0;

        using var timeoutSource = new CancellationTokenSource(context.Profile.Timeout);
        using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        Observe(AgentRunPhase.RunStarted, null, null, null);

        try
        {
            while (true)
            {
                Observe(AgentRunPhase.ModelRequested, null, null, null);
                var turn = await model.CompleteAsync(
                    new AgentModelRequest(request, allowedDescriptors, priorResults),
                    executionSource.Token)
                    .WaitAsync(executionSource.Token);
                executionSource.Token.ThrowIfCancellationRequested();

                if (turn.AssistantMessage is not null)
                {
                    return Complete(new AgentRunOutcome(AgentRunOutcomeKind.Result, "completed", turn.AssistantMessage, null));
                }

                var call = turn.ToolCall!;
                if (!toolRegistry.TryResolve(call, out var registration))
                {
                    return Complete(new AgentRunOutcome(AgentRunOutcomeKind.ValidationError, "tool_not_found", null, null));
                }

                var descriptor = registration.Descriptor;
                var decision = policy.Evaluate(context, descriptor);
                if (decision.OutcomeKind != AgentRunOutcomeKind.Result)
                {
                    return Complete(new AgentRunOutcome(
                        decision.OutcomeKind,
                        decision.SafeCode,
                        null,
                        descriptor.Name));
                }

                if (toolTurns >= context.Profile.MaxToolTurns)
                {
                    return Complete(new AgentRunOutcome(
                        AgentRunOutcomeKind.ValidationError,
                        "tool_turn_limit_exceeded",
                        null,
                        descriptor.Name));
                }

                Observe(AgentRunPhase.ToolValidationStarted, descriptor.Name, descriptor.Risk, null);
                executionSource.Token.ThrowIfCancellationRequested();
                var validation = registration.Tool.Validate(call);
                executionSource.Token.ThrowIfCancellationRequested();
                if (!validation.IsValid)
                {
                    return Complete(new AgentRunOutcome(
                        AgentRunOutcomeKind.ValidationError,
                        validation.SafeCode!,
                        null,
                        descriptor.Name));
                }

                var invocation = new AgentToolInvocation(runId, context, call);
                Observe(AgentRunPhase.ToolInvocationStarted, descriptor.Name, descriptor.Risk, null);
                executionSource.Token.ThrowIfCancellationRequested();
                var result = await registration.Tool.InvokeAsync(invocation, executionSource.Token)
                    .WaitAsync(executionSource.Token);
                executionSource.Token.ThrowIfCancellationRequested();
                if (result is null || !string.Equals(result.ToolName, descriptor.Name, StringComparison.Ordinal))
                {
                    return Complete(new AgentRunOutcome(
                        AgentRunOutcomeKind.ValidationError,
                        "tool_result_invalid",
                        null,
                        descriptor.Name));
                }

                priorResults.Add(result);
                toolTurns++;
            }
        }
        catch (OperationCanceledException exception) when (
            exception.CancellationToken == executionSource.Token
            && !cancellationToken.IsCancellationRequested
            && timeoutSource.IsCancellationRequested)
        {
            return Complete(new AgentRunOutcome(AgentRunOutcomeKind.ValidationError, "agent_timeout", null, null));
        }

        AgentRunOutcome Complete(AgentRunOutcome outcome)
        {
            Observe(AgentRunPhase.RunCompleted, null, null, outcome.Kind);
            return outcome;
        }

        void Observe(
            AgentRunPhase phase,
            string? toolName,
            AgentToolRisk? toolRisk,
            AgentRunOutcomeKind? outcomeKind) => observer.Observe(new AgentRunEvent(
                runId,
                context.Actor.UserId,
                context.CompanyId,
                context.Actor.CorrelationId,
                context.Channel,
                context.Profile.Id,
                phase,
                toolName,
                toolRisk,
                outcomeKind));
    }
}
