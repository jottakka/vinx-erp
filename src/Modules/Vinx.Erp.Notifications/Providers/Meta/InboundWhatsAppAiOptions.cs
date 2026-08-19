using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Perfis de IA do inbound do WhatsApp: um de texto para o turno único de
/// resposta e um de visão para o OCR (#135-B). Tudo-ou-nada no espírito do
/// W3: ambos ausentes mantém o host de pé e cada mensagem falha fechada com
/// <c>ai_unconfigured</c>; presente-porém-incompleto é erro de configuração
/// e derruba o boot.
/// </summary>
public sealed class InboundWhatsAppAiOptions
{
    public const string SectionName = "InboundWhatsAppAi";

    public OpenAiCompatibleOptions Text { get; set; } = new();

    public OpenAiCompatibleOptions Vision { get; set; } = new();

    public bool IsConfigured => IsProfilePresent(Text) && IsProfilePresent(Vision);

    /// <summary>
    /// Validação tudo-ou-nada: ambos perfis completos (visão precisa aceitar
    /// imagem) ou ambos vazios. Retorna null quando válido ou o motivo fixo
    /// (sem ecoar chave/endpoint) quando a configuração é parcial.
    /// </summary>
    public string? ValidateConfiguration()
    {
        var textPresent = IsProfilePresent(Text);
        var visionPresent = IsProfilePresent(Vision);
        if (textPresent != visionPresent)
        {
            return "inbound whatsapp ai profiles must be configured together";
        }

        if (!textPresent)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Text.Model) || string.IsNullOrWhiteSpace(Vision.Model))
        {
            return "inbound whatsapp ai profiles require a model";
        }

        if (!Vision.AllowImageInputs)
        {
            return "inbound whatsapp vision profile must allow image inputs";
        }

        return null;
    }

    /// <summary>Limites do pipeline de imagem compostos com o perfil de visão.</summary>
    public DocumentContextLimits CreateDocumentLimits() =>
        DocumentContextLimits.FromVisionProfile(Vision, new HashSet<string>(StringComparer.Ordinal));

    private static bool IsProfilePresent(OpenAiCompatibleOptions profile) =>
        profile.Endpoint is not null && !string.IsNullOrWhiteSpace(profile.ApiKey);
}
