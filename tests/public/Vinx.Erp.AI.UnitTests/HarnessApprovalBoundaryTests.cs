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
    public async Task Public_bundle_is_text_only_and_exposes_no_raw_agent_surface()
    {
        var bundleType = typeof(HarnessAgentBundle);
        var publicMethods = bundleType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(2, publicMethods.Length);
        AssertMethod(
            Assert.Single(publicMethods, method => method.Name == nameof(HarnessAgentBundle.CreateSessionAsync)),
            typeof(Task<AgentSession>),
            typeof(CancellationToken));
        AssertMethod(
            Assert.Single(publicMethods, method => method.Name == nameof(HarnessAgentBundle.RunUserTurnAsync)),
            typeof(Task<AgentResponse>),
            typeof(AgentSession),
            typeof(string),
            typeof(CancellationToken));

        var requestIdSourceType = typeof(HarnessExecutionRequestIdSource);
        Assert.True(requestIdSourceType.IsNotPublic);
        Assert.DoesNotContain(requestIdSourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), method =>
            method.Name is nameof(HarnessExecutionRequestIdSource.Bind) or nameof(HarnessExecutionRequestIdSource.Resolve));

        var bundle = VinxHarnessFactory.Create(new ScriptedChatClient(AssistantText("ok")), new HarnessProbeWriteCounter());
        var session = await bundle.CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => bundle.RunUserTurnAsync(session, " "));
    }

    [Fact]
    public void Public_approval_delivery_methods_require_proposal_and_unforgeable_permit()
    {
        var publicMethods = typeof(HarnessApprovalGate).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == nameof(HarnessApprovalGate.ApproveAsync))
            .ToArray();

        Assert.NotEmpty(publicMethods);
        Assert.All(publicMethods, method =>
        {
            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(Guid));
            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(HarnessApprovalDeliveryPermit));
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(ToolApprovalResponseContent));
        });
        Assert.Empty(typeof(HarnessApprovalDeliveryPermit).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public async Task Write_without_server_delivery_remains_pending_and_unexecuted()
    {
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"));
        var bundle = VinxHarnessFactory.Create(client, counter);
        var session = await bundle.CreateSessionAsync();

        var response = await bundle.RunUserTurnAsync(session, "write probe");

        Assert.Contains(response.Messages.SelectMany(message => message.Contents), content => content is ToolApprovalRequestContent);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Permit_consumes_once_and_records_only_bound_execution_request_id()
    {
        var executionRequestId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"),
            AssistantText("approved"));
        var bundle = VinxHarnessFactory.Create(client, counter);
        var session = await bundle.CreateSessionAsync();
        var pending = await bundle.RunUserTurnAsync(session, "write probe");
        var request = FindApprovalRequest(pending)!;
        bundle.RequestIdSource.Bind(executionRequestId);
        var gate = CreateGate();
        var permit = new HarnessApprovalDeliveryPermit(proposalId, "write-call");

        var response = await gate.ApproveAsync(
            bundle.Agent, session, request, proposalId, permit, ServerContext);

        Assert.Equal("approved", response.Text);
        Assert.Equal(1, counter.Count);
        Assert.Equal([executionRequestId], counter.RecordedRequestIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ApproveAsync(
            bundle.Agent, session, request, proposalId, permit, ServerContext));
        Assert.Equal(1, counter.Count);
    }

    [Theory]
    [InlineData("mismatched-call")]
    [InlineData("different-proposal")]
    [InlineData("extra-content")]
    [InlineData("missing-contents")]
    [InlineData("read-tool")]
    [InlineData("unknown-tool")]
    [InlineData("invalid-context")]
    [InlineData("wrong-decision")]
    public async Task Delivery_permit_negative_matrix_fails_closed_before_agent_execution(string caseName)
    {
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(AssistantText("must not run"));
        var bundle = VinxHarnessFactory.Create(client, counter);
        var session = await bundle.CreateSessionAsync();
        var proposalId = Guid.NewGuid();
        var request = new ToolApprovalRequestContent(
            "request", new FunctionCallContent("write-call", VinxToolPolicy.ProbeWriteToolName));
        var message = new ChatMessage(ChatRole.User, [request.CreateResponse(true)]);
        var pending = request;
        var expectedApproved = true;
        var permit = new HarnessApprovalDeliveryPermit(proposalId, "write-call");
        var context = ServerContext;

        switch (caseName)
        {
            case "mismatched-call":
                message = new ChatMessage(ChatRole.User,
                [new ToolApprovalResponseContent(
                    "other-call", true, new FunctionCallContent("other-call", VinxToolPolicy.ProbeWriteToolName))]);
                break;
            case "different-proposal":
                proposalId = Guid.NewGuid();
                break;
            case "extra-content":
                message = new ChatMessage(ChatRole.User, [request.CreateResponse(true), new TextContent("extra")]);
                break;
            case "missing-contents":
                message = new ChatMessage(ChatRole.User, []);
                break;
            case "read-tool":
                pending = new ToolApprovalRequestContent(
                    "request", new FunctionCallContent("read-call", VinxToolPolicy.ProbeStatusToolName));
                message = new ChatMessage(ChatRole.User, [pending.CreateResponse(true)]);
                permit = new HarnessApprovalDeliveryPermit(proposalId, "read-call");
                break;
            case "unknown-tool":
                pending = new ToolApprovalRequestContent(
                    "request", new FunctionCallContent("unknown-call", "UnknownTool"));
                message = new ChatMessage(ChatRole.User, [pending.CreateResponse(true)]);
                permit = new HarnessApprovalDeliveryPermit(proposalId, "unknown-call");
                break;
            case "invalid-context":
                context = new VinxExecutionContext(Guid.Empty, CompanyId);
                break;
            case "wrong-decision":
                expectedApproved = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName));
        }

        var requestCountBefore = client.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateGate().DeliverApprovalResponseAsync(
            bundle.Agent, session, message, pending, expectedApproved, proposalId, permit, context, CancellationToken.None));

        Assert.Equal(requestCountBefore, client.Requests.Count);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Approval_message_with_mismatched_call_id_fails_closed()
    {
        var counter = new HarnessProbeWriteCounter();
        var firstBundle = VinxHarnessFactory.Create(
            new ScriptedChatClient(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call")), counter);
        var firstSession = await firstBundle.CreateSessionAsync();
        var pending = await firstBundle.RunUserTurnAsync(firstSession, "write probe");
        var request = FindApprovalRequest(pending)!;
        var secondClient = new ScriptedChatClient(AssistantText("must not run"));
        var secondBundle = VinxHarnessFactory.Create(secondClient, counter);
        var secondSession = await secondBundle.CreateSessionAsync();
        var proposalId = Guid.NewGuid();
        var permit = new HarnessApprovalDeliveryPermit(proposalId, "write-call");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateGate().DeliverApprovalResponseAsync(
            secondBundle.Agent,
            secondSession,
            new ChatMessage(ChatRole.User, [request.CreateResponse(true)]),
            new ToolApprovalRequestContent("request", new FunctionCallContent("different-call", VinxToolPolicy.ProbeWriteToolName)),
            true,
            proposalId,
            permit,
            ServerContext,
            CancellationToken.None));

        Assert.Empty(secondClient.Requests);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void Evaluate_uses_only_trusted_context_and_rejects_multiple_requests()
    {
        var gate = CreateGate();
        var response = new AgentResponse(new ChatMessage(ChatRole.Assistant,
        [
            new ToolApprovalRequestContent("one", new FunctionCallContent("call-one", VinxToolPolicy.ProbeWriteToolName)),
            new ToolApprovalRequestContent("two", new FunctionCallContent("call-two", VinxToolPolicy.ProbeWriteToolName)),
        ]));

        var denied = gate.Evaluate(response, ServerContext);
        Assert.Equal(VinxApprovalOutcome.Denied, denied.Outcome);
        Assert.Null(denied.Request);

        var one = new AgentResponse(new ChatMessage(ChatRole.Assistant,
        [new ToolApprovalRequestContent("one", new FunctionCallContent("call-one", VinxToolPolicy.ProbeWriteToolName))]));
        Assert.Equal(VinxApprovalOutcome.Denied, gate.Evaluate(one, null).Outcome);
        Assert.Equal(VinxApprovalOutcome.PendingConfirmation, gate.Evaluate(one, ServerContext).Outcome);
    }

    private static HarnessApprovalGate CreateGate() =>
        new(NullLogger<HarnessApprovalGate>.Instance);

    private static void AssertMethod(MethodInfo method, Type returnType, params Type[] parameterTypes)
    {
        Assert.Equal(returnType, method.ReturnType);
        Assert.Equal(parameterTypes, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static ToolApprovalRequestContent? FindApprovalRequest(AgentResponse response) =>
        response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().SingleOrDefault();

    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage AssistantCall(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);
}
