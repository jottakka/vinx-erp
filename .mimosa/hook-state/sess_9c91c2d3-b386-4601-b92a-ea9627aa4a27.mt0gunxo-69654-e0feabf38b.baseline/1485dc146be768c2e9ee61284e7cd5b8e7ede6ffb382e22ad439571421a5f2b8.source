using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentRunnerOutcomeTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public async Task Tool_proposal_requires_confirmation_and_never_executes()
    {
        var outcome = await RunAsync(TestAgentModel.ToolCall("tool_x"));

        Assert.Equal(AgentOutcomeStatus.RequiresConfirmation, outcome.Status);
        Assert.NotEqual(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ToolProposalRequiresConfirmation, outcome.ReasonCode);
        Assert.Equal("tool_x", outcome.ProposedActionName);
        Assert.Null(outcome.Text);

        // Assert estrutural: a fronteira de modelo não recebe ferramentas — não
        // existe mecanismo de execução; AllowedTools vazio do catálogo já
        // garantido na Task 2.
        var parameters = typeof(IAgentModel).GetMethod(nameof(IAgentModel.GetResponseAsync))!.GetParameters();
        Assert.Equal(new[] { "messages", "selection", "cancellationToken" }, parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task Model_exception_returns_unavailable()
    {
        var outcome = await RunAsync(TestAgentModel.Throwing(new InvalidOperationException("provedor fora do ar")));

        Assert.Equal(AgentOutcomeStatus.Unavailable, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ModelUnavailable, outcome.ReasonCode);
        Assert.Null(outcome.Text);
        Assert.Null(outcome.ProposedActionName);
    }

    [Fact]
    public async Task Model_timeout_returns_timed_out()
    {
        // Perfil de teste com timeout curto; fake aguarda além dele. O cancela-
        // mento vem do token vinculado, não do externo: vira TimedOut, não throw.
        var outcome = await RunAsync(TestAgentModel.Delayed(TimeSpan.FromSeconds(2)), timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(AgentOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ModelTimeout, outcome.ReasonCode);
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(2), $"Encerrou antes do atraso total: {outcome.Elapsed}");
    }

    [Fact]
    public async Task External_cancellation_propagates()
    {
        var profile = TestProfile();
        var model = TestAgentModel.Delayed(TimeSpan.FromSeconds(2));
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Cancelamento do chamador não é outcome: a exceção propaga.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
                profile.ProfileId,
                "resuma X",
                AlfaCompanyId,
                cancelled.Token));
    }

    [Fact]
    public void Outcome_carries_only_safe_fields()
    {
        // Allowlist exata: qualquer campo novo (prompt, contexto, JWT, PII) quebra.
        var expected = new[]
        {
            nameof(AgentOutcome.Status),
            nameof(AgentOutcome.ReasonCode),
            nameof(AgentOutcome.Text),
            nameof(AgentOutcome.ProposedActionName),
            nameof(AgentOutcome.CorrelationId),
            nameof(AgentOutcome.Elapsed),
        };

        var actual = typeof(AgentOutcome)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name), actual);
    }

    private static async Task<AgentOutcome> RunAsync(TestAgentModel model, TimeSpan? timeout = null)
    {
        var profile = TestProfile(timeout);
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null);

        return await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma X",
            AlfaCompanyId,
            CancellationToken.None);
    }

    private const string TestProfileId = "test_summarize_v1";

    private static AgentTaskProfile TestProfile(TimeSpan? timeout = null) => AgentTaskProfile.CreateValidated(
        TestProfileId,
        "summarize",
        AgentJourneyScope.Company,
        AgentCapabilities.Read,
        new AgentModelSelection("test-provider", "test-model"),
        timeout ?? TimeSpan.FromSeconds(30),
        turnLimit: 1,
        maxTotalTokens: 8_000,
        allowedTools: Array.Empty<string>());
}
