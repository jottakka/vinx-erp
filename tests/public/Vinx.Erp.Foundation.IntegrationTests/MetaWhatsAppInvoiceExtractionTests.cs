using Vinx.Erp.AI;
using Vinx.Erp.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// O adapter Meta não extrai mais candidato financeiro. O resultado Pagaí
/// tipado vem do núcleo e o canal só pode expor a rota fixa de revisão Web.
/// </summary>
public sealed class MetaWhatsAppInvoiceExtractionTests
{
    [Fact]
    public void Materialized_proposal_maps_to_the_allow_listed_review_route()
    {
        var proposalId = Guid.Parse("d9510d25-1489-4b34-8931-8b940afccbb9");
        var result = AgentExecutionResult.Completed(
            new AgentOutcome(
                AgentOutcomeStatus.Completed,
                AgentOutcomeReason.Completed,
                Text: null,
                ProposedActionName: null,
                CorrelationId: Guid.NewGuid(),
                Elapsed: TimeSpan.Zero),
            proposalId);

        var reply = InboundAgentChannelAdapter.MapReply(
            result,
            new Uri("https://erp.example.test/"));

        Assert.Equal(
            "Documento pronto para revisão: https://erp.example.test/paguai?proposal=d9510d25-1489-4b34-8931-8b940afccbb9",
            reply);
    }

    [Theory]
    [InlineData(AgentExecutionDisposition.InProgress)]
    [InlineData(AgentExecutionDisposition.Ambiguous)]
    [InlineData(AgentExecutionDisposition.FailedClosed)]
    public void Non_completed_execution_never_maps_to_a_reply(
        AgentExecutionDisposition disposition)
    {
        var result = disposition switch
        {
            AgentExecutionDisposition.InProgress => AgentExecutionResult.InProgress(),
            AgentExecutionDisposition.Ambiguous => AgentExecutionResult.Ambiguous("provider_ambiguous"),
            AgentExecutionDisposition.FailedClosed => AgentExecutionResult.FailedClosed("capability_denied"),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null),
        };

        Assert.Null(InboundAgentChannelAdapter.MapReply(
            result,
            new Uri("https://erp.example.test/")));
    }
}
