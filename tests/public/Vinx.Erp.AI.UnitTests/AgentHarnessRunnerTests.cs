using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Models;
using Vinx.Erp.AI.Tools;
using Vinx.Erp.AI.UnitTests.Fixtures;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentHarnessRunnerTests
{
    [Fact]
    public async Task Run_returns_final_model_reply_without_invoking_a_tool()
    {
        var observer = new RecordingAgentRunObserver();
        var harness = CreateHarness(ScriptedAgentModel.Final("final answer"), observer);

        var outcome = await harness.RunAsync(CreateContext(), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Result, outcome.Kind);
        Assert.Equal("completed", outcome.SafeCode);
        Assert.Equal("final answer", outcome.AssistantMessage);
        Assert.Null(outcome.ToolName);
        Assert.Equal([AgentRunPhase.RunStarted, AgentRunPhase.ModelRequested, AgentRunPhase.RunCompleted], observer.Events.Select(item => item.Phase));
    }

    [Fact]
    public async Task Run_invokes_allowed_read_tool_then_requests_final_reply()
    {
        var tool = new RecordingAgentTool("payments.read");
        var observer = new RecordingAgentRunObserver();
        var model = new ScriptedAgentModel([
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", "{}")),
            AgentModelTurn.Final("read complete")]);
        var harness = CreateHarness(model, observer, tool);

        var outcome = await harness.RunAsync(CreateContext(), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Result, outcome.Kind);
        Assert.Equal("read complete", outcome.AssistantMessage);
        Assert.Single(tool.Validations);
        Assert.Single(tool.Invocations);
        Assert.Single(model.Requests.Skip(1).Single().PriorToolResults);
        Assert.Equal(
            [AgentRunPhase.RunStarted, AgentRunPhase.ModelRequested, AgentRunPhase.ToolValidationStarted,
                AgentRunPhase.ToolInvocationStarted, AgentRunPhase.ModelRequested, AgentRunPhase.RunCompleted],
            observer.Events.Select(item => item.Phase));
    }

    [Fact]
    public async Task Run_returns_safe_unknown_tool_outcome_without_observing_model_controlled_name()
    {
        const string unknownName = "forged.secret.tool";
        var observer = new RecordingAgentRunObserver();
        var harness = CreateHarness(ScriptedAgentModel.Tool(unknownName, "{}"), observer);

        var outcome = await harness.RunAsync(CreateContext(), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.ValidationError, outcome.Kind);
        Assert.Equal("tool_not_found", outcome.SafeCode);
        Assert.Null(outcome.ToolName);
        Assert.DoesNotContain(observer.Events, item => item.ToolName == unknownName);
        Assert.Equal([AgentRunPhase.RunStarted, AgentRunPhase.ModelRequested, AgentRunPhase.RunCompleted], observer.Events.Select(item => item.Phase));
    }

    [Fact]
    public async Task Run_requires_confirmation_before_validation_or_invocation()
    {
        var tool = new RecordingAgentTool("payments.draft_request", AgentToolRisk.InternalWrite);
        var harness = CreateHarness(ScriptedAgentModel.Tool("payments.draft_request", "{}"), new RecordingAgentRunObserver(), tool);

        var outcome = await harness.RunAsync(CreateContext("payments.draft_request"), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.RequiresConfirmation, outcome.Kind);
        Assert.Equal("confirmation_required", outcome.SafeCode);
        Assert.Empty(tool.Validations);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Run_requires_company_before_validation_or_invocation()
    {
        var tool = new RecordingAgentTool("payments.read", requiresExplicitCompany: true);
        var harness = CreateHarness(ScriptedAgentModel.Tool("payments.read", "{}"), new RecordingAgentRunObserver(), tool);

        var outcome = await harness.RunAsync(CreateContextWithoutCompany(), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.NeedsClarification, outcome.Kind);
        Assert.Equal("company_context_required", outcome.SafeCode);
        Assert.Empty(tool.Validations);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Run_returns_validation_failure_without_invocation()
    {
        var tool = new RecordingAgentTool("payments.read", validation: AgentToolValidationResult.Invalid("invalid_period"));
        var harness = CreateHarness(ScriptedAgentModel.Tool("payments.read", "{}"), new RecordingAgentRunObserver(), tool);

        var outcome = await harness.RunAsync(CreateContext(), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.ValidationError, outcome.Kind);
        Assert.Equal("invalid_period", outcome.SafeCode);
        Assert.Single(tool.Validations);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Run_returns_tool_failure_to_model_without_automatic_retry()
    {
        var tool = new RecordingAgentTool(
            "payments.read",
            invoke: (_, _) => Task.FromResult(RecordingAgentTool.CreateResult("payments.read", success: false, safeCode: "upstream_failed")));
        var model = new ScriptedAgentModel([
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", "{}")),
            AgentModelTurn.Final("cannot complete")]);
        var harness = CreateHarness(model, new RecordingAgentRunObserver(), tool);

        var outcome = await harness.RunAsync(CreateContext(), CreateRequest(), CancellationToken.None);

        Assert.Equal("cannot complete", outcome.AssistantMessage);
        Assert.Single(tool.Invocations);
        Assert.Single(model.Requests.Skip(1).Single().PriorToolResults);
        Assert.False(model.Requests.Skip(1).Single().PriorToolResults.Single().IsSuccess);
    }

    [Fact]
    public async Task Run_enforces_turn_limit_before_second_validation()
    {
        var tool = new RecordingAgentTool("payments.read");
        var model = new ScriptedAgentModel([
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", "{}")),
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", "{}"))]);
        var harness = CreateHarness(model, new RecordingAgentRunObserver(), tool);

        var outcome = await harness.RunAsync(CreateContext(maxToolTurns: 1), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.ValidationError, outcome.Kind);
        Assert.Equal("tool_turn_limit_exceeded", outcome.SafeCode);
        Assert.Single(tool.Validations);
        Assert.Single(tool.Invocations);
    }

    [Fact]
    public async Task Run_maps_profile_timeout_to_safe_outcome_when_caller_is_not_cancelled()
    {
        var harness = CreateHarness(new DelayedAgentModel(TimeSpan.FromSeconds(1)), new RecordingAgentRunObserver());

        var outcome = await harness.RunAsync(CreateContext(timeout: TimeSpan.FromMilliseconds(10)), CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.ValidationError, outcome.Kind);
        Assert.Equal("agent_timeout", outcome.SafeCode);
    }

    [Fact]
    public async Task Run_propagates_external_cancellation_after_profile_timeout_without_completion_event()
    {
        var expected = new OperationCanceledException(CancellationToken.None);
        var observer = new RecordingAgentRunObserver();
        var harness = CreateHarness(
            new TimeoutIgnoringCancellationThrowingModel(TimeSpan.FromMilliseconds(100), expected),
            observer);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.RunAsync(CreateContext(timeout: TimeSpan.FromMilliseconds(10)), CreateRequest(), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(CancellationToken.None, actual.CancellationToken);
        Assert.DoesNotContain(observer.Events, item => item.Phase == AgentRunPhase.RunCompleted);
    }

    [Fact]
    public async Task Run_propagates_caller_cancellation_intact()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var harness = CreateHarness(new DelayedAgentModel(TimeSpan.FromSeconds(1)), new RecordingAgentRunObserver());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunAsync(CreateContext(), CreateRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Run_stops_after_model_ignores_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new RecordingAgentTool("payments.read");
        var model = new CancellingAgentModel(
            cancellation,
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", "{}")));
        var harness = CreateHarness(model, new RecordingAgentRunObserver(), tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunAsync(CreateContext(), CreateRequest(), cancellation.Token));

        Assert.Empty(tool.Validations);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Run_stops_after_tool_ignores_caller_cancellation_without_next_model_call()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new RecordingAgentTool(
            "payments.read",
            invoke: (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(RecordingAgentTool.CreateResult("payments.read"));
            });
        var model = new IgnoringCancellationScriptedAgentModel([
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", "{}")),
            AgentModelTurn.Final("must not be requested")]);
        var harness = CreateHarness(model, new RecordingAgentRunObserver(), tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunAsync(CreateContext(), CreateRequest(), cancellation.Token));

        Assert.Single(tool.Invocations);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task Run_stops_after_validation_cancels_caller_before_invocation()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new RecordingAgentTool("payments.read", onValidate: cancellation.Cancel);
        var harness = CreateHarness(
            ScriptedAgentModel.Tool("payments.read", "{}"),
            new RecordingAgentRunObserver(),
            tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunAsync(CreateContext(), CreateRequest(), cancellation.Token));

        Assert.Single(tool.Validations);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Run_stops_after_invocation_observer_cancels_caller_before_invocation()
    {
        using var cancellation = new CancellationTokenSource();
        var observer = new RecordingAgentRunObserver(runEvent =>
        {
            if (runEvent.Phase == AgentRunPhase.ToolInvocationStarted) cancellation.Cancel();
        });
        var tool = new RecordingAgentTool("payments.read");
        var harness = CreateHarness(ScriptedAgentModel.Tool("payments.read", "{}"), observer, tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunAsync(CreateContext(), CreateRequest(), cancellation.Token));

        Assert.Single(tool.Validations);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Run_propagates_unexpected_model_exception_intact()
    {
        var expected = new InvalidOperationException("model failed");
        var harness = CreateHarness(new ThrowingAgentModel(expected), new RecordingAgentRunObserver());

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync(CreateContext(), CreateRequest(), CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Run_keeps_parallel_actor_company_and_correlation_isolated()
    {
        var observer = new RecordingAgentRunObserver();
        var harness = CreateHarness(new RecordingAgentModel(AgentModelTurn.Final("safe")), observer);
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();
        var alfa = Guid.NewGuid();
        var beta = Guid.NewGuid();
        var correlationA = Guid.NewGuid();
        var correlationB = Guid.NewGuid();

        var outcomes = await Task.WhenAll(
            harness.RunAsync(CreateContext(actorA, alfa, correlationA), CreateRequest(), CancellationToken.None),
            harness.RunAsync(CreateContext(actorB, beta, correlationB), CreateRequest(), CancellationToken.None));

        Assert.All(outcomes, outcome => Assert.Equal(AgentRunOutcomeKind.Result, outcome.Kind));
        Assert.All(observer.Events, runEvent => Assert.Contains(runEvent.CompanyId, new Guid?[] { alfa, beta }));
        Assert.Contains(observer.Events, item => item.ActorId == actorA && item.CorrelationId == correlationA && item.CompanyId == alfa);
        Assert.Contains(observer.Events, item => item.ActorId == actorB && item.CorrelationId == correlationB && item.CompanyId == beta);
    }

    [Fact]
    public async Task Run_exposes_only_profile_allowed_descriptors_to_model()
    {
        var model = new RecordingAgentModel(AgentModelTurn.Final("safe"));
        var harness = CreateHarness(
            model,
            new RecordingAgentRunObserver(),
            new RecordingAgentTool("payments.read"),
            new RecordingAgentTool("payments.admin"));

        await harness.RunAsync(CreateContext("payments.read"), CreateRequest(), CancellationToken.None);

        Assert.Equal(["payments.read"], model.Requests.Single().AllowedTools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task Run_builds_tool_invocation_from_trusted_context_and_sanitized_arguments()
    {
        var tool = new RecordingAgentTool("payments.read");
        var alpha = Guid.NewGuid();
        var harness = CreateHarness(
            new ScriptedAgentModel([
                AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", """{"company_id":"forged"}""")),
                AgentModelTurn.Final("safe")]),
            new RecordingAgentRunObserver(),
            tool);

        await harness.RunAsync(CreateContext(companyId: alpha), CreateRequest(), CancellationToken.None);

        Assert.Equal(alpha, tool.Invocations.Single().ExecutionContext.CompanyId);
        Assert.DoesNotContain("company_id", tool.Invocations.Single().Call.Arguments.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_events_omit_untrusted_sensitive_content_and_keep_safe_order()
    {
        const string userMessage = "USER-SECRET";
        const string modelAnswer = "MODEL-SECRET";
        const string ocrContent = "OCR-SECRET";
        const string rawArguments = "ARGUMENT-SECRET";
        var observer = new RecordingAgentRunObserver();
        var harness = CreateHarness(new ScriptedAgentModel([
            AgentModelTurn.Tool(ScriptedAgentModel.CreateCall("payments.read", """{"note":"ARGUMENT-SECRET"}""")),
            AgentModelTurn.Final(modelAnswer)]), observer, new RecordingAgentTool("payments.read"));
        var request = new AgentRunRequest(userMessage, [new AgentContextItem("ocr", ocrContent, 1)]);

        await harness.RunAsync(CreateContext(), request, CancellationToken.None);

        var json = JsonSerializer.Serialize(observer.Events);
        Assert.DoesNotContain(userMessage, json, StringComparison.Ordinal);
        Assert.DoesNotContain(modelAnswer, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ocrContent, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawArguments, json, StringComparison.Ordinal);
        Assert.All(observer.Events.Where(item => item.Phase != AgentRunPhase.RunCompleted), item => Assert.Null(item.OutcomeKind));
        Assert.Equal(AgentRunOutcomeKind.Result, observer.Events.Last().OutcomeKind);
        Assert.Single(observer.Events, item => item.Phase == AgentRunPhase.RunStarted);
        Assert.Single(observer.Events, item => item.Phase == AgentRunPhase.RunCompleted);
        Assert.True(Array.IndexOf(observer.Events.Select(item => item.Phase).ToArray(), AgentRunPhase.ToolValidationStarted)
            < Array.IndexOf(observer.Events.Select(item => item.Phase).ToArray(), AgentRunPhase.ToolInvocationStarted));
    }

    private static AgentHarnessRunner CreateHarness(IAgentModel model, IAgentRunObserver observer, params IAgentTool[] tools) =>
        new(model, new AgentToolRegistry(tools), new AgentRunPolicy(), observer);

    private static AgentExecutionContext CreateContext(
        string allowedTool = "payments.read",
        Guid? companyId = null,
        int maxToolTurns = 2,
        TimeSpan? timeout = null) =>
        CreateContext(Guid.NewGuid(), companyId ?? Guid.NewGuid(), Guid.NewGuid(), allowedTool, maxToolTurns, timeout);

    private static AgentExecutionContext CreateContext(Guid actorId, Guid companyId, Guid correlationId) =>
        CreateContext(actorId, companyId, correlationId, "payments.read", 2, null);

    private static AgentExecutionContext CreateContext(
        Guid actorId,
        Guid? companyId,
        Guid correlationId,
        string allowedTool,
        int maxToolTurns,
        TimeSpan? timeout) => new(
        new ActorContext(actorId, correlationId),
        companyId,
        AgentChannel.Web,
        [allowedTool],
        new AgentTaskProfile("payments.summary", [allowedTool], maxToolTurns, timeout ?? TimeSpan.FromSeconds(5)));

    private static AgentExecutionContext CreateContextWithoutCompany() =>
        CreateContext(Guid.NewGuid(), null, Guid.NewGuid(), "payments.read", 2, null);

    private static AgentRunRequest CreateRequest() => new("summarize payments", []);
}
