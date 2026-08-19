using System.Text.RegularExpressions;

namespace Vinx.Erp.AI.Guardrails;

/// <summary>
/// Redação mínima de dados sensíveis do texto de saída: substitui e-mails por
/// marcador fixo; as demais categorias entram na fatia de redação completa.
/// </summary>
public sealed partial class SensitiveOutputRedactor
{
    private const string RedactedEmail = "[email-redigido]";

    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    /// <summary>Redige o texto de saída; texto nulo ou vazio retorna inalterado.</summary>
    public string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return EmailPattern().Replace(text, RedactedEmail);
    }
}
