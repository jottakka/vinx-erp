using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Models;
using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.UnitTests.Fixtures;

internal sealed class ScriptedAgentModel : IAgentModel
{
    private readonly object sync = new();
    private readonly Queue<AgentModelTurn> turns;
    private readonly List<AgentModelRequest> requests = [];

    public ScriptedAgentModel(params AgentModelTurn[] turns)
    {
        this.turns = new Queue<AgentModelTurn>(turns);
    }

    public IReadOnlyList<AgentModelRequest> Requests
    {
        get
        {
            lock (sync)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<AgentModelTurn> CompleteAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            requests.Add(request);
            return Task.FromResult(turns.Dequeue());
        }
    }

    public static AgentModelTurn Final(string message) => new(message, null);

    public static AgentModelTurn Tool(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new AgentModelTurn(null, new AgentToolCall(name, document.RootElement));
    }
}

internal sealed class CallbackAgentModel(
    Func<AgentModelRequest, CancellationToken, Task<AgentModelTurn>> complete) : IAgentModel
{
    public Task<AgentModelTurn> CompleteAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken) => complete(request, cancellationToken);
}

internal sealed class BlockingAgentModel : IAgentModel
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<AgentModelTurn> CompleteAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Blocking model completed unexpectedly.");
    }
}

internal sealed class RecordingAgentRunObserver : IAgentRunObserver
{
    private readonly object sync = new();
    private readonly List<AgentRunEvent> events = [];

    public IReadOnlyList<AgentRunEvent> Events
    {
        get
        {
            lock (sync)
            {
                return events.ToArray();
            }
        }
    }

    public ValueTask ObserveAsync(AgentRunEvent runEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            events.Add(runEvent);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingAgentTool : IAgentTool
{
    private readonly object sync = new();
    private readonly AgentToolResult result;
    private readonly List<AgentToolCall> validations = [];
    private readonly List<AgentToolInvocation> invocations = [];

    public RecordingAgentTool(
        string name,
        AgentToolRisk risk = AgentToolRisk.ReadOnly,
        AgentToolValidationResult? validation = null,
        AgentToolResult? result = null)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });

        Descriptor = new AgentToolDescriptor(
            name,
            "1.0",
            schema,
            schema,
            "paguai.read",
            risk,
            true,
            false,
            new AgentMcpAnnotations(
                risk == AgentToolRisk.ReadOnly,
                risk != AgentToolRisk.ReadOnly,
                true,
                false));

        Validation = validation ?? new AgentToolValidationResult(true, null);
        this.result = result ?? new AgentToolResult(
            name,
            true,
            "ok",
            JsonSerializer.SerializeToElement(new { ok = true }));
    }

    public AgentToolDescriptor Descriptor { get; }
    public AgentToolValidationResult Validation { get; }

    public IReadOnlyList<AgentToolCall> Validations
    {
        get
        {
            lock (sync)
            {
                return validations.ToArray();
            }
        }
    }

    public IReadOnlyList<AgentToolInvocation> Invocations
    {
        get
        {
            lock (sync)
            {
                return invocations.ToArray();
            }
        }
    }

    public AgentToolValidationResult Validate(AgentToolCall call)
    {
        lock (sync)
        {
            validations.Add(call);
        }

        return Validation;
    }

    public Task<AgentToolResult> InvokeAsync(
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            invocations.Add(invocation);
        }

        return Task.FromResult(result);
    }
}
