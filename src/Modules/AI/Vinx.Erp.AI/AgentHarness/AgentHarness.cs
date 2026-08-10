using Vinx.Erp.AI.Models;
using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.AgentHarness;

public sealed class AgentHarness
{
    private readonly IAgentModel model;
    private readonly AgentToolRegistry toolRegistry;
    private readonly AgentRunPolicy policy;
    private readonly IAgentRunObserver observer;

    public AgentHarness(
        IAgentModel model,
        AgentToolRegistry toolRegistry,
        AgentRunPolicy policy,
        IAgentRunObserver observer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observer);

        this.model = model;
        this.toolRegistry = toolRegistry;
        this.policy = policy;
        this.observer = observer;
    }

    public async Task<AgentRunOutcome> RunAsync(
        AgentExecutionContext context,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = Guid.NewGuid();
        var allowedDescriptors = toolRegistry.Descriptors
            .Where(descriptor => context.Profile.AllowedTools.Contains(descriptor.Name))
            .ToArray();
        var priorToolResults = new List<AgentToolResult>();
        var toolTurns = 0;

        using var timeoutSource = new CancellationTokenSource(context.Profile.Timeout);
        using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        await ObserveAsync(runId, context, AgentRunPhase.Received, null, null, cancellationToken);

        try
        {
            while (true)
            {
                executionSource.Token.ThrowIfCancellationRequested();
                await ObserveAsync(runId, context, AgentRunPhase.ModelRequested, null, null, cancellationToken);

                var turn = await model.CompleteAsync(
                    new AgentModelRequest(
                        request,
                        allowedDescriptors,
                        priorToolResults),
                    executionSource.Token);
                executionSource.Token.ThrowIfCancellationRequested();

                if (turn.AssistantMessage is { } assistantMessage)
                {
                    return await CompleteAsync(
                        runId,
                        context,
                        new AgentRunOutcome(AgentRunOutcomeKind.Result, "result", assistantMessage, null),
                        null,
                        cancellationToken);
                }

                var call = turn.ToolCall!;
                if (!toolRegistry.TryGet(call.Name, out var tool))
                {
                    await ObserveAsync(runId, context, AgentRunPhase.ToolProposed, null, null, cancellationToken);

                    return await CompleteAsync(
                        runId,
                        context,
                        new AgentRunOutcome(AgentRunOutcomeKind.Denied, "tool_unknown", null, null),
                        null,
                        cancellationToken);
                }

                var descriptor = tool.Descriptor;
                await ObserveAsync(runId, context, AgentRunPhase.ToolProposed, descriptor, null, cancellationToken);

                if (toolTurns >= context.Profile.MaxToolTurns)
                {
                    return await CompleteAsync(
                        runId,
                        context,
                        new AgentRunOutcome(
                            AgentRunOutcomeKind.ValidationError,
                            "tool_turn_limit_exceeded",
                            null,
                            descriptor.Name),
                        descriptor,
                        cancellationToken);
                }

                executionSource.Token.ThrowIfCancellationRequested();
                var policyDecision = policy.Evaluate(context, descriptor);
                if (policyDecision.OutcomeKind != AgentRunOutcomeKind.Result)
                {
                    return await CompleteAsync(
                        runId,
                        context,
                        new AgentRunOutcome(
                            policyDecision.OutcomeKind,
                            policyDecision.SafeCode,
                            null,
                            descriptor.Name),
                        descriptor,
                        cancellationToken);
                }

                executionSource.Token.ThrowIfCancellationRequested();
                var validation = tool.Validate(call);
                await ObserveAsync(
                    runId,
                    context,
                    AgentRunPhase.ToolValidated,
                    descriptor,
                    validation.IsValid ? null : AgentRunOutcomeKind.ValidationError,
                    cancellationToken);

                if (!validation.IsValid)
                {
                    return await CompleteAsync(
                        runId,
                        context,
                        new AgentRunOutcome(
                            AgentRunOutcomeKind.ValidationError,
                            validation.SafeCode!,
                            null,
                            descriptor.Name),
                        descriptor,
                        cancellationToken);
                }

                await ObserveAsync(runId, context, AgentRunPhase.ToolInvoked, descriptor, null, cancellationToken);
                executionSource.Token.ThrowIfCancellationRequested();

                var toolResult = await tool.InvokeAsync(
                    new AgentToolInvocation(runId, context, call),
                    executionSource.Token);
                executionSource.Token.ThrowIfCancellationRequested();

                priorToolResults.Add(toolResult);
                toolTurns++;
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(
                runId,
                context,
                new AgentRunOutcome(AgentRunOutcomeKind.ValidationError, "agent_timeout", null, null),
                null,
                cancellationToken);
        }
    }

    private async Task<AgentRunOutcome> CompleteAsync(
        Guid runId,
        AgentExecutionContext context,
        AgentRunOutcome outcome,
        AgentToolDescriptor? descriptor,
        CancellationToken cancellationToken)
    {
        await ObserveAsync(
            runId,
            context,
            AgentRunPhase.Completed,
            descriptor,
            outcome.Kind,
            cancellationToken);

        return outcome;
    }

    private ValueTask ObserveAsync(
        Guid runId,
        AgentExecutionContext context,
        AgentRunPhase phase,
        AgentToolDescriptor? descriptor,
        AgentRunOutcomeKind? outcome,
        CancellationToken cancellationToken) => observer.ObserveAsync(
            new AgentRunEvent(
                runId,
                context.Actor.UserId,
                context.CompanyId,
                context.Actor.CorrelationId,
                context.Channel,
                context.Profile.Id,
                descriptor?.Name,
                descriptor?.Risk,
                phase,
                outcome),
            cancellationToken);
}
