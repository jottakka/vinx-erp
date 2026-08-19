using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Corpo mínimo da resposta de texto dentro da janela de 24h aberta pela
/// mensagem recebida: apenas o destinatário e o texto, nenhum outro dado.
/// </summary>
public sealed record MetaWhatsAppTextMessage(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] MetaWhatsAppTextBody Text
);

public sealed record MetaWhatsAppTextBody(
    [property: JsonPropertyName("body")] string Body
);

public static class MetaWhatsAppReplyMapper
{
    /// <summary>Limite documentado do WhatsApp Cloud API para o corpo de texto.</summary>
    public const int MaximumBodyCharacters = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Mapeia a resposta com truncamento determinístico no limite da API:
    /// nunca excede, nunca descarta a mensagem inteira por tamanho.
    /// </summary>
    public static MetaWhatsAppTextMessage Map(string waId, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var normalized = body.Trim();
        if (normalized.Length > MaximumBodyCharacters)
        {
            normalized = normalized[..(MaximumBodyCharacters - 1)] + "…";
        }

        return new MetaWhatsAppTextMessage("whatsapp", waId, "text", new MetaWhatsAppTextBody(normalized));
    }

    public static string Serialize(MetaWhatsAppTextMessage message) =>
        JsonSerializer.Serialize(message, SerializerOptions);
}
