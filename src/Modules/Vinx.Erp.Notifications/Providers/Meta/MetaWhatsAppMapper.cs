using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Corpo mínimo de envio: destinatário, template aprovado e, no máximo, o
/// deep-link como única variável — nenhum outro dado de negócio.
/// </summary>
public sealed record MetaWhatsAppMessage(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("template")] MetaWhatsAppTemplate Template
);

public sealed record MetaWhatsAppTemplate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("language")] MetaWhatsAppLanguage Language,
    [property: JsonPropertyName("components")] IReadOnlyList<MetaWhatsAppComponent>? Components = null
);

public sealed record MetaWhatsAppLanguage(
    [property: JsonPropertyName("code")] string Code
);

public sealed record MetaWhatsAppComponent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("parameters")] IReadOnlyList<MetaWhatsAppParameter> Parameters
);

public sealed record MetaWhatsAppParameter(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text
);

public static class MetaWhatsAppMapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Mapeia somente o template aprovado e o deep-link opcional; o link é a
    /// base da web app mais o selector relativo, sem segredo ou PII.
    /// </summary>
    public static MetaWhatsAppMessage Map(
        string phoneNumber,
        string metaTemplateName,
        string metaLanguage,
        string? deepLinkSelector,
        string webAppBaseUrl
    )
    {
        IReadOnlyList<MetaWhatsAppComponent>? components = null;
        if (!string.IsNullOrWhiteSpace(deepLinkSelector))
        {
            var link = webAppBaseUrl.TrimEnd('/') + "/"
                + deepLinkSelector.TrimStart('/');
            components =
            [
                new MetaWhatsAppComponent(
                    "body",
                    [new MetaWhatsAppParameter("text", link)]
                ),
            ];
        }

        return new MetaWhatsAppMessage(
            "whatsapp",
            phoneNumber,
            "template",
            new MetaWhatsAppTemplate(
                metaTemplateName,
                new MetaWhatsAppLanguage(metaLanguage),
                components
            )
        );
    }

    public static string Serialize(MetaWhatsAppMessage message) =>
        JsonSerializer.Serialize(message, SerializerOptions);
}
