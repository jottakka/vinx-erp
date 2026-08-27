using Vinx.Erp.AI;
using Vinx.Erp.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class InboundWhatsAppAgentAdapterTests
{
    [Fact]
    public void Paguai_result_uses_only_the_server_generated_review_path()
    {
        var proposalId = Guid.Parse("507d5665-d4d8-4db5-9cb6-d1fc43492eb6");
        var outcome = new AgentOutcome(
            AgentOutcomeStatus.Completed,
            AgentOutcomeReason.Completed,
            Text: null,
            ProposedActionName: null,
            CorrelationId: Guid.Parse("3d827f31-571c-4f41-b702-14f4f737758f"),
            Elapsed: TimeSpan.Zero);

        var reply = InboundAgentChannelAdapter.MapReply(
            AgentExecutionResult.Completed(outcome, proposalId),
            new Uri("https://erp.example.test/base/"));

        Assert.Equal(
            "Documento pronto para revisão: https://erp.example.test/base/paguai/draft-proposals/507d5665-d4d8-4db5-9cb6-d1fc43492eb6",
            reply);
    }
}
