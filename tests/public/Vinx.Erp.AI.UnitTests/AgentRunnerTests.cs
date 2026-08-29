using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentRunnerTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public async Task Empty_message_returns_invalid_without_resolution_or_model()
    {
        var (runner, resolver, model) = CreateRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            TestAgentModel.Text("Resumo Alfa"));

        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var outcome = await runner.RunAsync(actor, TestProfileId, "  ", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Invalid, outcome.Status);
        Assert.Equal(AgentOutcomeReason.MessageEmpty, outcome.ReasonCode);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
        Assert.Null(outcome.Text);
        Assert.Equal(actor.CorrelationId, outcome.CorrelationId);
    }

    [Fact]
    public async Task Unknown_profile_returns_invalid_without_model()
    {
        // Sem lookup injetado: catálogo server-side padrão; id inexistente não resolve.
        var resolver = new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId));
        var model = TestAgentModel.Text("Resumo Alfa");
        var runner = new AgentRunner(resolver, model, NullLogger<AgentRunner>.Instance);

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), "profile_inexistente_v9", "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Invalid, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ProfileUnknown, outcome.ReasonCode);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Missing_company_returns_needs_clarification_without_model()
    {
        var (runner, resolver, model) = CreateRunner(
            new SequenceContextResolver(new AgentContextResolution(
                AgentContextResolutionStatus.NeedsClarification,
                AgentOutcomeReason.CompanyRequired)),
            TestAgentModel.Text("Resumo Alfa"));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", requestedCompanyId: null, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.NeedsClarification, outcome.Status);
        Assert.Equal(AgentOutcomeReason.CompanyRequired, outcome.ReasonCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
        Assert.Null(outcome.Text);
    }

    [Fact]
    public async Task Denied_company_returns_denied_without_model()
    {
        var (runner, resolver, model) = CreateRunner(
            new SequenceContextResolver(new AgentContextResolution(
                AgentContextResolutionStatus.Denied,
                AgentOutcomeReason.CompanyNotAuthorized)),
            TestAgentModel.Text("Resumo Alfa"));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Denied, outcome.Status);
        Assert.Equal(AgentOutcomeReason.CompanyNotAuthorized, outcome.ReasonCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
        Assert.Null(outcome.Text);
    }

    [Fact]
    public async Task Revoked_membership_between_runs_denies_second_run()
    {
        // Snapshot revogado entre execuções: a 2ª resolução falha fechada e o
        // modelo não é chamado de novo (CallCount permanece 1 após duas runs).
        var (runner, resolver, model) = CreateRunner(
            new SequenceContextResolver(
                SequenceContextResolver.Resolved(AlfaCompanyId),
                new AgentContextResolution(AgentContextResolutionStatus.Denied, AgentOutcomeReason.CompanyNotAuthorized)),
            TestAgentModel.Text("Resumo Alfa"));
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        var first = await runner.RunAsync(actor, TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);
        var second = await runner.RunAsync(actor, TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, first.Status);
        Assert.Equal(AgentOutcomeStatus.Denied, second.Status);
        Assert.Equal(AgentOutcomeReason.CompanyNotAuthorized, second.ReasonCode);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task Unavailable_context_returns_unavailable_without_model()
    {
        var (runner, resolver, model) = CreateRunner(
            new SequenceContextResolver(new AgentContextResolution(
                AgentContextResolutionStatus.Unavailable,
                AgentOutcomeReason.ContextUnavailable)),
            TestAgentModel.Text("Resumo Alfa"));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Unavailable, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ContextUnavailable, outcome.ReasonCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Provider_not_configured_returns_unavailable_without_model()
    {
        // Perfil aprovado do catálogo com ModelSelection null: fail-closed até
        // o provedor ser atribuído (#135), mesmo com modelo registrado.
        var resolver = new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId));
        var model = TestAgentModel.Text("Resumo Alfa");
        var runner = new AgentRunner(resolver, model, NullLogger<AgentRunner>.Instance);

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            "resuma X",
            AlfaCompanyId,
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Unavailable, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ProviderNotConfigured, outcome.ReasonCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Success_flow_calls_model_once_with_user_message()
    {
        var profile = TestProfile();
        var selection = profile.ModelSelection!;
        var (runner, _, model) = CreateRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            TestAgentModel.Text("Resumo Alfa"),
            profile);
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        var outcome = await runner.RunAsync(actor, TestProfileId, "resuma X da Alfa", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(AgentOutcomeReason.Completed, outcome.ReasonCode);
        Assert.Equal("Resumo Alfa", outcome.Text);
        Assert.Equal(actor.CorrelationId, outcome.CorrelationId);
        Assert.True(outcome.Elapsed >= TimeSpan.Zero);
        Assert.Equal(1, model.CallCount);

        // Turn normalizado: uma única mensagem, exatamente o texto do usuário.
        var messages = Assert.Single(model.ReceivedMessages);
        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("resuma X da Alfa", message.Text);
        Assert.Equal(selection, Assert.Single(model.ReceivedSelections));
    }

    [Fact]
    public async Task No_model_call_happens_before_resolution()
    {
        // Assert transversal dos casos 1-7: todo caminho curto (validação,
        // resolução, provider) conclui SEM chamada de modelo; a ordem
        // registrada acima prova que o modelo só entra após Resolved.
        var model = TestAgentModel.Text("Resumo Alfa");
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        var invalid = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            model,
            NullLogger<AgentRunner>.Instance);
        var invalidOutcome = await invalid.RunAsync(actor, TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);
        Assert.Equal(AgentOutcomeStatus.Invalid, invalidOutcome.Status);

        var catalogRunner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            model,
            NullLogger<AgentRunner>.Instance);
        var providerOutcome = await catalogRunner.RunAsync(
            actor, AgentTaskProfileCatalog.ReadOnlySummarizeProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);
        Assert.Equal(AgentOutcomeStatus.Unavailable, providerOutcome.Status);

        var deniedRunner = CreateRunner(
            new SequenceContextResolver(new AgentContextResolution(
                AgentContextResolutionStatus.Denied,
                AgentOutcomeReason.CapabilityDenied)),
            model).Runner;
        var deniedOutcome = await deniedRunner.RunAsync(actor, TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);
        Assert.Equal(AgentOutcomeStatus.Denied, deniedOutcome.Status);

        Assert.Equal(0, model.CallCount);
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
        allowedTools: []);

    private static (AgentRunner Runner, SequenceContextResolver Resolver, TestAgentModel Model) CreateRunner(
        SequenceContextResolver resolver,
        TestAgentModel model,
        AgentTaskProfile? profile = null)
    {
        var resolvedProfile = profile ?? TestProfile();
        var runner = new AgentRunner(
            resolver,
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == resolvedProfile.ProfileId ? resolvedProfile : null);
        return (runner, resolver, model);
    }
}
