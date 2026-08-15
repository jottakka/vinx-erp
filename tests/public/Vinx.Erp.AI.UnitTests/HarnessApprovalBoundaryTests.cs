using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class HarnessApprovalBoundaryTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly VinxExecutionContext ServerContext = new(ActorId, CompanyId);

    [Fact]
    public async Task Read_only_user_turn_executes_through_real_harness()
    {
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeStatusToolName, "read-call"),
            AssistantText("read complete"));
        var bundle = VinxHarnessFactory.Create(client, counter);
        var session = await bundle.CreateSessionAsync();

        var response = await bundle.RunUserTurnAsync(session, "read probe");

        Assert.Equal("read complete", response.Text);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Public_surface_is_text_only_and_has_no_approval_delivery()
    {
        var bundleMethods = typeof(HarnessAgentBundle)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(2, bundleMethods.Length);
        AssertMethod(
            Assert.Single(bundleMethods, method => method.Name == nameof(HarnessAgentBundle.CreateSessionAsync)),
            typeof(Task<AgentSession>),
            typeof(CancellationToken));
        AssertMethod(
            Assert.Single(bundleMethods, method => method.Name == nameof(HarnessAgentBundle.RunUserTurnAsync)),
            typeof(Task<AgentResponse>),
            typeof(AgentSession),
            typeof(string),
            typeof(CancellationToken));
        Assert.Empty(typeof(HarnessAgentBundle)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        var gateMethods = typeof(HarnessApprovalGate)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Collection(gateMethods, method => Assert.Equal(nameof(HarnessApprovalGate.Evaluate), method.Name));
        var factoryMethod = Assert.Single(
            typeof(VinxHarnessFactory)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == nameof(VinxHarnessFactory.Create));
        AssertMethod(
            factoryMethod,
            typeof(HarnessAgentBundle),
            typeof(IChatClient),
            typeof(Microsoft.Extensions.Logging.ILoggerFactory));
        Assert.False(typeof(HarnessProbeWriteCounter).IsPublic);

        var bundle = VinxHarnessFactory.Create(new ScriptedChatClient(AssistantText("ok")));
        var session = await bundle.CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => bundle.RunUserTurnAsync(session, " "));
    }

    [Fact]
    public async Task Write_without_server_delivery_remains_pending_and_unexecuted()
    {
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"));
        var bundle = VinxHarnessFactory.Create(client, counter);
        var session = await bundle.CreateSessionAsync();

        var response = await bundle.RunUserTurnAsync(session, "write probe");

        Assert.Contains(
            response.Messages.SelectMany(message => message.Contents),
            content => content is ToolApprovalRequestContent);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Outgoing_tool_list_contains_only_registered_vinx_tools()
    {
        var client = new ScriptedChatClient(AssistantText("ok"));
        var bundle = VinxHarnessFactory.Create(client, new HarnessProbeWriteCounter());
        var session = await bundle.CreateSessionAsync();

        await bundle.RunUserTurnAsync(session, "hello");

        var request = Assert.Single(client.Requests);
        Assert.Equal(
            [VinxToolPolicy.ProbeStatusToolName, VinxToolPolicy.ProbeWriteToolName],
            request.ToolNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Iteration_bound_is_respected()
    {
        var calls = Enumerable.Range(1, 12)
            .Select(index => AssistantCall(VinxToolPolicy.ProbeStatusToolName, $"call-{index}"))
            .ToArray();
        var client = new ScriptedChatClient(calls);
        var bundle = VinxHarnessFactory.Create(client, new HarnessProbeWriteCounter());
        var session = await bundle.CreateSessionAsync();

        await bundle.RunUserTurnAsync(session, "repeat read probe");

        Assert.True(
            client.Requests.Count <= VinxHarnessFactory.MaximumIterationsPerRequest + 1,
            $"Expected at most {VinxHarnessFactory.MaximumIterationsPerRequest + 1} calls; got {client.Requests.Count}.");
    }

    [Fact]
    public void Evaluate_uses_only_trusted_server_context()
    {
        var gate = CreateGate();
        var response = ResponseWithApprovalRequests(
            new ToolApprovalRequestContent(
                "write",
                new FunctionCallContent("write-call", VinxToolPolicy.ProbeWriteToolName)));

        Assert.Equal(VinxApprovalOutcome.Denied, gate.Evaluate(response, null).Outcome);
        Assert.Equal(
            VinxApprovalOutcome.Denied,
            gate.Evaluate(response, new VinxExecutionContext(Guid.Empty, CompanyId)).Outcome);
        Assert.Equal(VinxApprovalOutcome.PendingConfirmation, gate.Evaluate(response, ServerContext).Outcome);
    }

    [Fact]
    public void Evaluate_rejects_unknown_read_and_multiple_requests()
    {
        var gate = CreateGate();

        var unknown = gate.Evaluate(
            ResponseWithApprovalRequests(
                new ToolApprovalRequestContent("unknown", new FunctionCallContent("call", "UnknownTool"))),
            ServerContext);
        var read = gate.Evaluate(
            ResponseWithApprovalRequests(
                new ToolApprovalRequestContent(
                    "read",
                    new FunctionCallContent("read-call", VinxToolPolicy.ProbeStatusToolName))),
            ServerContext);
        var missingCallId = gate.Evaluate(
            ResponseWithApprovalRequests(
                new ToolApprovalRequestContent(
                    "missing call id",
                    new FunctionCallContent(string.Empty, VinxToolPolicy.ProbeWriteToolName))),
            ServerContext);
        var multiple = gate.Evaluate(
            ResponseWithApprovalRequests(
                new ToolApprovalRequestContent(
                    "one",
                    new FunctionCallContent("call-one", VinxToolPolicy.ProbeWriteToolName)),
                new ToolApprovalRequestContent(
                    "two",
                    new FunctionCallContent("call-two", VinxToolPolicy.ProbeWriteToolName))),
            ServerContext);

        Assert.Equal(VinxApprovalOutcome.Denied, unknown.Outcome);
        Assert.Null(unknown.ToolName);
        Assert.Equal(VinxApprovalOutcome.Denied, read.Outcome);
        Assert.Equal(VinxApprovalOutcome.Denied, missingCallId.Outcome);
        Assert.Equal(VinxApprovalOutcome.Denied, multiple.Outcome);
        Assert.Null(multiple.Request);
    }

    private static HarnessApprovalGate CreateGate() =>
        new(NullLogger<HarnessApprovalGate>.Instance);

    private static void AssertMethod(MethodInfo method, Type returnType, params Type[] parameterTypes)
    {
        Assert.Equal(returnType, method.ReturnType);
        Assert.Equal(parameterTypes, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static AgentResponse ResponseWithApprovalRequests(params ToolApprovalRequestContent[] requests) =>
        new(new ChatMessage(ChatRole.Assistant, [.. requests]));

    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage AssistantCall(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);
}
