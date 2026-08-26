using System.Text;

namespace Vinx.Erp.Notifications.Providers.Telegram;

/// <summary>
/// Converte o destino aprovado em corpo mínimo do Bot API, preservando texto
/// literal sem parse mode, interpolação ou campos adicionais.
/// </summary>
public static class TelegramMapper
{
    private const string MaximumPrivateChatId = "4503599627370495";
    private static readonly char[] SqlBtrimCharacters = [' ', '\t', '\n', '\v', '\f', '\r'];

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

        if (!HasValidPlainText(text))
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

    /// <summary>
    /// Espelha o CHECK SQL: somente U+0020, tab, LF, VT, FF e CR são
    /// removidos das bordas, e o limite conta pontos de código Unicode.
    /// </summary>
    private static bool HasValidPlainText(string? text)
    {
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim(SqlBtrimCharacters);
        return trimmed.Length > 0
            && trimmed.Length == text.Length
            && text.EnumerateRunes().Count() is >= 1 and <= 4096;
    }
}
