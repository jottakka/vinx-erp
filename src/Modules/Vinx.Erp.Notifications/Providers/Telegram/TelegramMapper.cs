namespace Vinx.Erp.Notifications.Providers.Telegram;

/// <summary>
/// Converte o destino aprovado em corpo mínimo do Bot API, preservando texto
/// literal sem parse mode, interpolação ou campos adicionais.
/// </summary>
public static class TelegramMapper
{
    private const string MaximumPrivateChatId = "4503599627370495";

    /// <summary>
    /// Valida o identificador privado canônico e o texto aprovado antes de
    /// criar a mensagem que será serializada pelo client.
    /// </summary>
    public static TelegramMessage Map(string chatId, string text)
    {
        if (!IsCanonicalPrivateChatId(chatId))
        {
            throw new ArgumentException("Telegram chat ID is invalid.", nameof(chatId));
        }

        if (
            string.IsNullOrWhiteSpace(text)
            || text.Length > 4096
            || !string.Equals(text, text.Trim(), StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("Telegram message text is invalid.", nameof(text));
        }

        return new TelegramMessage(chatId, text);
    }

    private static bool IsCanonicalPrivateChatId(string? chatId) =>
        chatId is { Length: >= 1 and <= 16 }
        && chatId[0] is >= '1' and <= '9'
        && chatId.All(character => character is >= '0' and <= '9')
        && (
            chatId.Length < MaximumPrivateChatId.Length
            || string.CompareOrdinal(chatId, MaximumPrivateChatId) <= 0
        );
}
