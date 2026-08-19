using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentRunnerIsolationTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid BetaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    [Fact]
    public async Task Concurrent_actors_do_not_mix_context_or_outcome()
    {
        // Ator/empresa/correlation próprios por run; fakes por ator. O runner é
        // stateless: cada outcome casa 1:1 com o seu ator.
        var actorAlfa = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var actorBeta = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var (runnerAlfa, resolverAlfa, modelAlfa) = CreateRunner(AlfaCompanyId, "Resumo Alfa");
        var (runnerBeta, resolverBeta, modelBeta) = CreateRunner(BetaCompanyId, "Resumo Beta");

        var alfaTask = runnerAlfa.RunAsync(actorAlfa, TestProfileId, "resuma Alfa", AlfaCompanyId, CancellationToken.None);
        var betaTask = runnerBeta.RunAsync(actorBeta, TestProfileId, "resuma Beta", BetaCompanyId, CancellationToken.None);
        await Task.WhenAll(alfaTask, betaTask);
        var alfa = await alfaTask;
        var beta = await betaTask;

        Assert.Equal(AgentOutcomeStatus.Completed, alfa.Status);
        Assert.Equal("Resumo Alfa", alfa.Text);
        Assert.Equal(actorAlfa.CorrelationId, alfa.CorrelationId);
        Assert.NotEqual(actorBeta.CorrelationId, alfa.CorrelationId);

        Assert.Equal(AgentOutcomeStatus.Completed, beta.Status);
        Assert.Equal("Resumo Beta", beta.Text);
        Assert.Equal(actorBeta.CorrelationId, beta.CorrelationId);

        Assert.Equal(1, modelAlfa.CallCount);
        Assert.Equal(1, modelBeta.CallCount);
        Assert.Equal(1, resolverAlfa.CallCount);
        Assert.Equal(1, resolverBeta.CallCount);
    }

    [Fact]
    public async Task Concurrent_same_actor_different_companies()
    {
        // Mesmo ator, Alfa e Beta no snapshot: cada run pede a sua empresa e o
        // Text do fake distinto por mensagem prova o pareamento com a requisição.
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var (runnerAlfa, resolverAlfa, modelAlfa) = CreateRunner(AlfaCompanyId, "Resumo Alfa");
        var (runnerBeta, resolverBeta, modelBeta) = CreateRunner(BetaCompanyId, "Resumo Beta");

        var alfaTask = runnerAlfa.RunAsync(actor, TestProfileId, "resuma Alfa", AlfaCompanyId, CancellationToken.None);
        var betaTask = runnerBeta.RunAsync(actor, TestProfileId, "resuma Beta", BetaCompanyId, CancellationToken.None);
        await Task.WhenAll(alfaTask, betaTask);
        var alfa = await alfaTask;
        var beta = await betaTask;

        Assert.Equal("Resumo Alfa", alfa.Text);
        Assert.Equal("Resumo Beta", beta.Text);
        Assert.Equal(actor.CorrelationId, alfa.CorrelationId);
        Assert.Equal(actor.CorrelationId, beta.CorrelationId);

        Assert.Equal(AlfaCompanyId, Assert.Single(resolverAlfa.ReceivedCompanyIds));
        Assert.Equal(BetaCompanyId, Assert.Single(resolverBeta.ReceivedCompanyIds));
        Assert.Equal("resuma Alfa", Assert.Single(Assert.Single(modelAlfa.ReceivedMessages)).Text);
        Assert.Equal("resuma Beta", Assert.Single(Assert.Single(modelBeta.ReceivedMessages)).Text);
        Assert.Equal(1, modelAlfa.CallCount);
        Assert.Equal(1, modelBeta.CallCount);
    }

    private const string TestProfileId = "test_summarize_v1";

    private static (AgentRunner Runner, SequenceContextResolver Resolver, TestAgentModel Model) CreateRunner(
        Guid companyId,
        string responseText)
    {
        var profile = AgentTaskProfile.CreateValidated(
            TestProfileId,
            "summarize",
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            new AgentModelSelection("test-provider", "test-model"),
            TimeSpan.FromSeconds(30),
            turnLimit: 1,
            maxTotalTokens: 8_000,
            allowedTools: Array.Empty<string>());
        var resolver = new SequenceContextResolver(SequenceContextResolver.Resolved(companyId));
        var model = TestAgentModel.Text(responseText);
        var runner = new AgentRunner(
            resolver,
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null);
        return (runner, resolver, model);
    }
}
