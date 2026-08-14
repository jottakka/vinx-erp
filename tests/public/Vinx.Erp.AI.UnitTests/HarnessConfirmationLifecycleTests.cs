using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Approvals;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

/// <summary>
/// Ciclo de vida da confirmação durável orquestrada por
/// <see cref="HarnessConfirmationCoordinator"/>: single-use, expiração,
/// semântica pós-crash, single-flight e idempotência da escrita por
/// <c>RequestId</c> de execução.
/// </summary>
public sealed class HarnessConfirmationLifecycleTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly VinxExecutionContext ServerContext = new(ActorId, CompanyId);
    private static readonly JsonElement Payload = JsonDocument.Parse("""{"alvo":"sonda"}""").RootElement.Clone();

    [Fact]
    public async Task ConfirmedProposal_ExecutesOnce_AndRemovesInProcessState()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var (agent, session, request) = await PreparePendingWriteAsync(counter, executionRequestId);
        var coordinator = CreateCoordinator(ConfirmApprovalOutcome.Claimed);
        var proposalId = Guid.NewGuid();

        coordinator.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());

        Assert.Equal(ConfirmApprovalOutcome.Claimed, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(1, counter.Count);
        Assert.Equal(0, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task ConflictingConfirmation_RemovesState_AndDoesNotExecute()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var (agent, session, request) = await PreparePendingWriteAsync(counter, executionRequestId);
        var coordinator = CreateCoordinator(ConfirmApprovalOutcome.Conflict);
        var proposalId = Guid.NewGuid();

        coordinator.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());

        Assert.Equal(ConfirmApprovalOutcome.Conflict, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, counter.Count);
        Assert.Equal(0, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task ExpiredProposal_IsUnavailable_AndNeverExecutes()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var (agent, session, request) = await PreparePendingWriteAsync(counter, executionRequestId);
        var coordinator = CreateCoordinator(ConfirmApprovalOutcome.Claimed);
        var proposalId = Guid.NewGuid();

        coordinator.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMilliseconds(50));
        await Task.Delay(TimeSpan.FromMilliseconds(120));

        var result = await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());

        Assert.Equal(ConfirmApprovalOutcome.ProposalUnavailable, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, counter.Count);
        Assert.Equal(0, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task ProcessRestart_InvalidatesLiveProposal_WithoutPromisingResponseReplay()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var (agent, session, request) = await PreparePendingWriteAsync(counter, executionRequestId);
        var proposalId = Guid.NewGuid();

        var beforeRestart = CreateCoordinator(ConfirmApprovalOutcome.Claimed);
        beforeRestart.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

        // O coordenador pós-reinício é um processo novo: o registro em memória
        // foi perdido, então a proposta viva não é mais retomável.
        var afterRestart = CreateCoordinator(ConfirmApprovalOutcome.Replay);
        var result = await afterRestart.ConfirmAsync(proposalId, Guid.NewGuid());

        Assert.Equal(ConfirmApprovalOutcome.ProposalUnavailable, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Veto_RemovesState_AndDoesNotExecute()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "chamada-1"),
            AssistantText("veto recebido"));
        var agent = VinxHarnessFactory.Create(client, counter, executionRequestId);
        var session = await agent.CreateSessionAsync();
        var pending = await agent.RunAsync("registre a sonda", session);
        var request = FindApprovalRequest(pending)!;
        var coordinator = CreateCoordinator(ConfirmApprovalOutcome.Claimed);
        var proposalId = Guid.NewGuid();

        coordinator.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

        var vetoed = await coordinator.VetoAsync(proposalId);

        Assert.Equal("veto recebido", vetoed.Text);
        Assert.Equal(0, counter.Count);
        Assert.Equal(0, coordinator.ActiveProposalCount);

        var afterVeto = await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        Assert.Equal(ConfirmApprovalOutcome.ProposalUnavailable, afterVeto.Outcome);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task ConcurrentConfirmationsWithSameRequestId_ExecuteExactlyOnce()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var (agent, session, request) = await PreparePendingWriteAsync(counter, executionRequestId);
        var coordinator = CreateCoordinator(ConfirmApprovalOutcome.Claimed);
        var proposalId = Guid.NewGuid();

        coordinator.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

        var confirmationRequestId = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => coordinator.ConfirmAsync(proposalId, confirmationRequestId)));

        // Single-flight: apenas o vencedor executa; a escrita acontece uma vez.
        Assert.Equal(1, counter.Count);
        Assert.Contains(outcomes, outcome => outcome.Outcome == ConfirmApprovalOutcome.Claimed);
        Assert.Equal(0, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task ReplayedConfirmation_WithSameRequestId_DoesNotResumeSessionTwice()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();
        var (agent, session, request) = await PreparePendingWriteAsync(counter, executionRequestId);
        var coordinator = CreateCoordinator(ConfirmApprovalOutcome.Claimed);
        var proposalId = Guid.NewGuid();

        coordinator.Register(
            proposalId, Guid.NewGuid(), ServerContext, agent, session, request,
            Payload, executionRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

        var confirmationRequestId = Guid.NewGuid();
        var first = await coordinator.ConfirmAsync(proposalId, confirmationRequestId);
        var second = await coordinator.ConfirmAsync(proposalId, confirmationRequestId);

        Assert.Equal(ConfirmApprovalOutcome.Claimed, first.Outcome);
        // Após o consumo o estado em processo foi removido; o replay não retoma
        // a sessão novamente.
        Assert.Equal(ConfirmApprovalOutcome.ProposalUnavailable, second.Outcome);
        Assert.Equal(1, counter.Count);
        Assert.Equal(0, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task UnmatchedApprovalResponse_NoOpBecomesExplicitError()
    {
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "chamada-1"));
        var agent = VinxHarnessFactory.Create(client, counter, Guid.NewGuid());
        var session = await agent.CreateSessionAsync();
        var pending = await agent.RunAsync("registre a sonda", session);
        var request = FindApprovalRequest(pending)!;

        // Resposta de aprovação sem vínculo com a solicitação pendente: o gate
        // lança erro explícito (fail-closed) em vez de um no-op silencioso.
        var fabricatedMessage = new ChatMessage(
            ChatRole.User,
            [new ToolApprovalResponseContent(
                "chamada-fabricada",
                true,
                new FunctionCallContent("chamada-fabricada", VinxToolPolicy.ProbeWriteToolName))]);

        Assert.Throws<InvalidOperationException>(
            () => HarnessApprovalGate.ThrowIfUnmatchedApprovalResponse(fabricatedMessage, request));

        // Respostas correspondentes passam pela guarda.
        var matchedMessage = new ChatMessage(ChatRole.User, [request.CreateResponse(true)]);
        HarnessApprovalGate.ThrowIfUnmatchedApprovalResponse(matchedMessage, request);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void RequestAwareWrite_IsIdempotentByExecutionRequestId()
    {
        var counter = new HarnessProbeWriteCounter();
        var executionRequestId = Guid.NewGuid();

        var first = counter.RecordWrite(executionRequestId);
        var replay = counter.RecordWrite(executionRequestId);
        var other = counter.RecordWrite(Guid.NewGuid());

        Assert.Equal(first, replay);
        Assert.Equal(2, counter.Count);
        Assert.NotEqual(first, other);
    }

    private static HarnessConfirmationCoordinator CreateCoordinator(ConfirmApprovalOutcome outcome) =>
        new(
            (_, _, _, _, _, _, _) => Task.FromResult(outcome),
            new HarnessApprovalGate(NullLogger<HarnessApprovalGate>.Instance));

    private static async Task<(HarnessAgent Agent, AgentSession Session, ToolApprovalRequestContent Request)>
        PreparePendingWriteAsync(HarnessProbeWriteCounter counter, Guid executionRequestId)
    {
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "chamada-1"),
            AssistantText("escrita confirmada"));
        var agent = VinxHarnessFactory.Create(client, counter, executionRequestId);
        var session = await agent.CreateSessionAsync();
        var pending = await agent.RunAsync("registre a sonda", session);
        var request = FindApprovalRequest(pending)!;
        return (agent, session, request);
    }

    private static ToolApprovalRequestContent? FindApprovalRequest(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent request)
                {
                    return request;
                }
            }
        }

        return null;
    }

    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage AssistantCall(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);
}
