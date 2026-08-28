extern alias workerhost;

using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Wolverine;
using Xunit;
using WorkerTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Hosts.IntegrationTests;

public sealed class TelegramAgentHandlerTests
{
    [Fact]
    public void Telegram_receipt_handler_keeps_the_technical_store_and_closed_agent_adapter_separate()
    {
        var constructor = Assert.Single(
            typeof(WorkerTypes.InboundTelegramTextReceivedHandler).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal(typeof(FoundationDbContext), parameters[0].ParameterType);
        Assert.Equal(typeof(InboundAgentChannelAdapter), parameters[2].ParameterType);
        // A publicação do envelope é transacional (ADR 0006): o handler
        // depende do outbox do EF, não do bus direto.
        Assert.Equal(
            typeof(Wolverine.EntityFrameworkCore.IDbContextOutbox),
            parameters[3].ParameterType);
    }

    [Fact]
    public void Telegram_agent_and_provider_outbox_envelopes_remain_opaque()
    {
        var agent = new InboundAgentRequestReceived(Guid.NewGuid(), AgentChannelKind.Telegram);
        var delivery = new NotificationDeliveryRequested(Guid.NewGuid());

        Assert.Equal(AgentChannelKind.Telegram, agent.Channel);
        Assert.NotEqual(Guid.Empty, agent.InboundMessageId);
        Assert.NotEqual(Guid.Empty, delivery.DeliveryId);
        Assert.Equal(2, agent.GetType().GetProperties().Length);
        Assert.Single(delivery.GetType().GetProperties());
    }
}
