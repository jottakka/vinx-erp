using System.Security.Cryptography;
using System.Text;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Compara o segredo enviado pelo Telegram sem normalizá-lo e com tempo fixo
/// apenas quando os buffers UTF-8 têm o mesmo tamanho.
/// </summary>
public static class TelegramWebhookVerifier
{
    public static bool Verify(string? providedToken, string expectedToken)
    {
        if (providedToken is null)
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
