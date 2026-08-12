using System.Collections.Concurrent;
using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Models;
using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.UnitTests.Fixtures;

internal sealed class ScriptedAgentModel(IEnumerable<AgentModelTurn> turns) : IAgentModel
{
    private readonly ConcurrentQueue<AgentModelTurn> turns = new(turns);

    public ConcurrentQueue<AgentModelRequest> Requests { get; } = new();

    public async Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return turns.TryDequeue(out var turn)
            ? turn
            : throw new InvalidOperationException("No scripted model turn remains.");
    }

    public static ScriptedAgentModel Final(string message) => new([AgentModelTurn.Final(message)]);

    public static ScriptedAgentModel Tool(string name, string arguments) => new([AgentModelTurn.Tool(CreateCall(name, arguments))]);

    public static AgentToolCall CreateCall(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new AgentToolCall(name, document.RootElement);
    }
}

internal sealed class DelayedAgentModel(TimeSpan delay) : IAgentModel
{
    public async Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return AgentModelTurn.Final("late");
    }
}

internal sealed class NonCooperativeAgentModel : IAgentModel
{
    private readonly TaskCompletionSource<AgentModelTurn> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken) => completion.Task;
}

internal sealed class ThrowingAgentModel(Exception exception) : IAgentModel
{
    public Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AgentModelTurn>(exception);
}

internal sealed class CancellingAgentModel(
    CancellationTokenSource callerCancellation,
    AgentModelTurn turn) : IAgentModel
{
    public Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        callerCancellation.Cancel();
        return Task.FromResult(turn);
    }
}

internal sealed class IgnoringCancellationScriptedAgentModel(IEnumerable<AgentModelTurn> turns) : IAgentModel
{
    private readonly ConcurrentQueue<AgentModelTurn> turns = new(turns);

    public ConcurrentQueue<AgentModelRequest> Requests { get; } = new();

    public Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(turns.TryDequeue(out var turn)
            ? turn
            : throw new InvalidOperationException("No scripted model turn remains."));
    }
}

internal sealed class TimeoutIgnoringCancellationThrowingModel(
    TimeSpan delay,
    OperationCanceledException exception) : IAgentModel
{
    public async Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(delay);
        throw exception;
    }
}

internal sealed class RecordingAgentModel(AgentModelTurn turn) : IAgentModel
{
    public ConcurrentQueue<AgentModelRequest> Requests { get; } = new();

    public Task<AgentModelTurn> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(turn);
    }
}

internal sealed class RecordingAgentTool : IAgentTool
{
    private readonly Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> invoke;
    private readonly Action? onValidate;

    public RecordingAgentTool(
        string name,
        AgentToolRisk risk = AgentToolRisk.ReadOnly,
        bool requiresExplicitCompany = false,
        AgentToolValidationResult? validation = null,
        Action? onValidate = null,
        Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>>? invoke = null)
    {
        Descriptor = CreateDescriptor(name, risk, requiresExplicitCompany);
        Validation = validation ?? AgentToolValidationResult.Valid();
        this.onValidate = onValidate;
        this.invoke = invoke ?? ((_, _) => Task.FromResult(CreateResult(name)));
    }

    public AgentToolDescriptor Descriptor { get; }
    public AgentToolValidationResult Validation { get; set; }
    public ConcurrentQueue<AgentToolCall> Validations { get; } = new();
    public ConcurrentQueue<AgentToolInvocation> Invocations { get; } = new();

    public AgentToolValidationResult Validate(AgentToolCall call)
    {
        Validations.Enqueue(call);
        onValidate?.Invoke();
        return Validation;
    }

    public async Task<AgentToolResult> InvokeAsync(AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Enqueue(invocation);
        return await invoke(invocation, cancellationToken);
    }

    public static AgentToolDescriptor CreateDescriptor(
        string name,
        AgentToolRisk risk = AgentToolRisk.ReadOnly,
        bool requiresExplicitCompany = false)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var output = JsonDocument.Parse("{\"type\":\"object\"}");
        return new AgentToolDescriptor(
            name,
            "1.0.0",
            schema.RootElement,
            output.RootElement,
            name,
            risk,
            requiresExplicitCompany,
            requiresIdempotency: risk != AgentToolRisk.ReadOnly,
            new AgentToolMcpHints(
                name,
                readOnlyHint: risk == AgentToolRisk.ReadOnly,
                idempotentHint: risk != AgentToolRisk.ReadOnly));
    }

    public static AgentToolResult CreateResult(string name, bool success = true, string safeCode = "tool_succeeded")
    {
        using var payload = JsonDocument.Parse("{\"result\":\"safe\"}");
        return new AgentToolResult(name, success, safeCode, payload.RootElement);
    }
}

internal sealed class RecordingAgentRunObserver(Action<AgentRunEvent>? onObserve = null) : IAgentRunObserver
{
    public ConcurrentQueue<AgentRunEvent> Events { get; } = new();

    public void Observe(AgentRunEvent runEvent)
    {
        Events.Enqueue(runEvent);
        onObserve?.Invoke(runEvent);
    }
}
