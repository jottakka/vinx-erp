namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Configuração do adapter WhatsApp Cloud API; segredos vêm exclusivamente do
/// secret store e nunca aparecem em log, argumento ou resposta.
/// </summary>
public sealed class MetaWhatsAppOptions
{
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("META_WHATSAPP_PHONE_NUMBER_ID")]
    public string PhoneNumberId { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("META_WHATSAPP_ACCESS_TOKEN")]
    public string AccessToken { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("META_WHATSAPP_API_BASE_URL")]
    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("META_WHATSAPP_WEB_APP_BASE_URL")]
    public string WebAppBaseUrl { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("META_WHATSAPP_APP_SECRET")]
    public string AppSecret { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("META_WHATSAPP_WEBHOOK_VERIFY_TOKEN")]
    public string WebhookVerifyToken { get; set; } = string.Empty;

    public string SendEndpoint =>
        $"{ApiBaseUrl.TrimEnd('/')}/v22.0/{PhoneNumberId.TrimEnd('/')}/messages";

    /// <summary>
    /// O webhook é opcional no ApiHost: sem o conjunto completo de segredos o
    /// host sobe e o endpoint responde 503; nunca aceita sem validação.
    /// </summary>
    public bool WebhookIsConfigured =>
        !string.IsNullOrWhiteSpace(AppSecret)
        && !string.IsNullOrWhiteSpace(WebhookVerifyToken)
        && !string.IsNullOrWhiteSpace(WebAppBaseUrl);

    public static bool IsAllowedHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.Scheme == Uri.UriSchemeHttps;
}
