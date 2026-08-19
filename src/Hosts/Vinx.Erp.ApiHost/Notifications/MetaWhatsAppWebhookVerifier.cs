using System.Security.Cryptography;
using System.Text;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Verificação da assinatura X-Hub-Signature-256 do webhook da Meta sobre os
/// bytes brutos do corpo, em tempo constante; deve ser chamada antes de
/// qualquer parse do conteúdo.
/// </summary>
public static class MetaWhatsAppWebhookVerifier
{
    public static bool Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader, string appSecret)
    {
        if (
            string.IsNullOrWhiteSpace(signatureHeader)
            || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal)
        )
        {
            return false;
        }

        var expectedHex = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), rawBody)
        ).ToLowerInvariant();
        var providedHex = signatureHeader["sha256=".Length..].Trim();
        if (providedHex.Length != expectedHex.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(providedHex),
            Encoding.ASCII.GetBytes(expectedHex)
        );
    }

    public static string Compute(ReadOnlySpan<byte> rawBody, string appSecret) =>
        "sha256="
            + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), rawBody)
            ).ToLowerInvariant();
}
