extern alias workerhost;

using System.Text.Json;
using Vinx.Erp.Notifications;
using Xunit;
using WorkerTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Hosts.IntegrationTests;

public sealed class InboundWhatsAppAgentHandlerTests
{
    [Fact]
    public void Durable_envelope_contains_only_the_opaque_message_id_and_channel()
    {
        var envelope = new InboundAgentRequestReceived(
            Guid.Parse("467602fb-dd23-48de-ac5d-88aeef4b7e9b"),
            AgentChannelKind.WhatsApp);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        Assert.Equal(
            ["InboundMessageId", "Channel"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void Handler_depends_on_the_shared_channel_adapter()
    {
        var constructor = Assert.Single(
            typeof(WorkerTypes.InboundAgentRequestReceivedHandler).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal(typeof(InboundAgentChannelAdapter), parameters[0].ParameterType);
        Assert.Equal(2, parameters.Length);
    }
}
