using System.Text.Json;
using Vinx.Erp.Notifications.Providers.Meta;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class MetaWhatsAppReplyContractTests
{
    [Fact]
    public void Map_builds_the_minimal_text_envelope()
    {
        var message = MetaWhatsAppReplyMapper.Map("556198132901", "  olá  ");

        Assert.Equal("whatsapp", message.MessagingProduct);
        Assert.Equal("556198132901", message.To);
        Assert.Equal("text", message.Type);
        Assert.Equal("olá", message.Text.Body);
    }

    [Fact]
    public void Map_truncates_deterministically_at_the_documented_limit()
    {
        var body = new string('a', MetaWhatsAppReplyMapper.MaximumBodyCharacters + 500);

        var message = MetaWhatsAppReplyMapper.Map("556198132901", body);

        Assert.Equal(MetaWhatsAppReplyMapper.MaximumBodyCharacters, message.Text.Body.Length);
        Assert.EndsWith("…", message.Text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_carries_only_recipient_and_body()
    {
        var message = MetaWhatsAppReplyMapper.Map("556198132901", "resposta");

        using var document = JsonDocument.Parse(MetaWhatsAppReplyMapper.Serialize(message));
        var root = document.RootElement;
        var properties = root
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["messaging_product", "to", "type", "text"], properties);
        Assert.Equal("resposta", root.GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public void Map_rejects_empty_inputs()
    {
        Assert.Throws<ArgumentException>(
            () => MetaWhatsAppReplyMapper.Map("", "resposta")
        );
        Assert.Throws<ArgumentException>(
            () => MetaWhatsAppReplyMapper.Map("556198132901", "  ")
        );
    }
}
