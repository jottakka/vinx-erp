using System.Text.RegularExpressions;
using Vinx.Erp.Foundation.Configuration;

namespace Vinx.Erp.Notifications.Providers.Telegram;

/// <summary>
/// Configuração do Bot API do Telegram. A ausência de configuração mantém o
/// worker inicializável; o host valida configurações parciais no startup.
/// </summary>
public sealed class TelegramOptions
{
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("TELEGRAM_BOT_TOKEN")]
    public string BotToken { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("TELEGRAM_API_BASE_URL")]
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("TELEGRAM_WEBHOOK_SECRET_TOKEN")]
    public string WebhookSecretToken { get; set; } = string.Empty;

    /// <summary>Indica se o envio possui token e base HTTPS utilizáveis.</summary>
    public bool OutboundIsConfigured =>
        !string.IsNullOrWhiteSpace(BotToken)
        && IsSafeBotToken(BotToken)
        && IsAllowedHttpsUrl(ApiBaseUrl);

    /// <summary>Indica se o segredo de webhook possui a sintaxe aceita pelo Telegram.</summary>
    public bool WebhookIsConfigured => IsValidWebhookSecret(WebhookSecretToken);

    /// <summary>Aplica a política HTTPS a uma base sem query ou fragmento.</summary>
    public static bool IsAllowedHttpsUrl(string value) =>
        AllowedUriPolicy.IsAllowedHttps(value)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Query);

    /// <summary>Valida a gramática oficial do segredo de webhook.</summary>
    public static bool IsValidWebhookSecret(string? value) =>
        value is not null
        && Regex.IsMatch(
            value,
            "\\A[A-Za-z0-9_-]{1,256}\\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
        );

    private static bool IsSafeBotToken(string value) =>
        value.All(character =>
            character is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or ':'
                or '_'
                or '-'
        );
}
