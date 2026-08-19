using System.Text;
using System.Text.RegularExpressions;

namespace Vinx.Erp.AI.Guardrails;

/// <summary>Desfecho da redação: texto transformado e indicador de alteração.</summary>
public sealed record RedactionResult(string? RedactedText, bool RedactedSomething);

/// <summary>
/// Redação determinística de conteúdo sensível do texto de saída por padrões
/// fixos de regex (sem LLM, sem heurística difusa), substituindo cada categoria
/// por marcador PT estável sem ecoar o conteúdo original: e-mail →
/// [email-redigido]; CPF (11 dígitos, formatado ou cru) e CNPJ (14, avaliado
/// antes para não dividir 14 em dois 11) → [identificador-redigido]; dados
/// bancários (agência dddd[-d] e conta ddddd[-d]/ddddddd[-d] com prefixo
/// contextual "agência"/"conta"/"account" PT/EN, e IBAN-like) →
/// [dado-bancario-redigido]; marcadores médicos de lista FECHADA →
/// [conteudo-medico-redigido]; URL com host fora da allow-list →
/// [link-removido] — casam URLs com esquema http(s) e também sem esquema
/// iniciadas por www. (domínio nu sem esquema NÃO é casado; lacuna
/// documentada). Host allow-listado permanece intacto, comparação
/// OrdinalIgnoreCase e host exato tolerando um rótulo inicial www. (o
/// prefixo http:// usado apenas para parse nunca altera o texto preservado).
/// Ordem: URLs primeiro (segmento allow-listado é preservado byte a byte e
/// protegido das demais regras), depois CNPJ, CPF, dados bancários,
/// marcadores médicos e e-mail.
/// </summary>
public sealed partial class SensitiveOutputRedactor(IReadOnlySet<string> allowedLinkHosts)
{
    private const string EmailToken = "[email-redigido]";
    private const string IdentifierToken = "[identificador-redigido]";
    private const string BankToken = "[dado-bancario-redigido]";
    private const string MedicalToken = "[conteudo-medico-redigido]";
    private const string LinkToken = "[link-removido]";

    private static readonly char[] UrlTrailingPunctuation = ['.', ',', ';', ':', '!', '?', '"', '\''];

    private readonly HashSet<string> allowedHosts =
        new(allowedLinkHosts ?? throw new ArgumentNullException(nameof(allowedLinkHosts)), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("(?<![\\w@.])(?:https?://|www\\.)\\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // CNPJ antes do CPF: sequência de 14 dígitos é um identificador único, nunca dois de 11.
    [GeneratedRegex("(?<!\\d)\\d{14}(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RawCnpjPattern();

    [GeneratedRegex("(?<!\\d)\\d{2}\\.\\d{3}\\.\\d{3}/\\d{4}-\\d{2}(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex FormattedCnpjPattern();

    [GeneratedRegex("(?<!\\d)\\d{11}(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RawCpfPattern();

    [GeneratedRegex("(?<!\\d)\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex FormattedCpfPattern();

    // Número bancário só redige com prefixo contextual imediato; o rótulo é preservado.
    [GeneratedRegex("(ag[êe]ncia|conta|account)\\s*(?:n[.ºo]*\\s*)?[:\\-]?\\s*((?:\\d{4}|\\d{5}|\\d{7})(?:-\\d)?)(?![\\d-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BankDataPattern();

    [GeneratedRegex("\\b[A-Z]{2}\\d{2}[A-Z0-9]{8,26}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex IbanLikePattern();

    // Lista FECHADA de marcadores médicos; termos novos só entram por mudança de política.
    [GeneratedRegex("diagn[oó]stico\\s*:|prescri[cç][aã]o|laudo m[eé]dico|cid\\s*-?\\s*1[01]|dosagem\\s*(?:de\\s*)?(?:\\d+(?:[.,]\\d+)?\\s*)?mg\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MedicalMarkerPattern();

    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    /// <summary>Redige o texto de saída; texto nulo ou vazio retorna inalterado sem redação.</summary>
    public RedactionResult Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RedactionResult(text, RedactedSomething: false);
        }

        // URLs primeiro: allow-listado permanece intacto (nem os dígitos do path são tocados),
        // não listado vira marcador; as demais regras rodam só fora dos spans de URL.
        var builder = new StringBuilder(text.Length);
        var redactedSomething = false;
        var lastCopied = 0;
        foreach (Match url in UrlPattern().Matches(text))
        {
            var segment = RedactPatterns(text[lastCopied..url.Index]);
            redactedSomething |= segment != text[lastCopied..url.Index];
            builder.Append(segment);

            var (replacement, removed) = ResolveUrl(url.Value);
            builder.Append(replacement);
            redactedSomething |= removed;
            lastCopied = url.Index + url.Length;
        }

        var tail = RedactPatterns(text[lastCopied..]);
        redactedSomething |= tail != text[lastCopied..];
        builder.Append(tail);

        return new RedactionResult(builder.ToString(), redactedSomething);
    }

    private (string Replacement, bool Removed) ResolveUrl(string url)
    {
        var trimmed = url.TrimEnd(UrlTrailingPunctuation);

        // Match sem esquema (www.): o prefixo http:// existe só para o parse do
        // host; o texto preservado é sempre o match original.
        var parseTarget = trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "http://" + trimmed
            : trimmed;
        if (Uri.TryCreate(parseTarget, UriKind.Absolute, out var parsed) && IsHostAllowed(parsed.Host))
        {
            return (url, Removed: false);
        }

        return (LinkToken + url[trimmed.Length..], Removed: true);
    }

    private bool IsHostAllowed(string host) =>
        allowedHosts.Contains(host)
        || (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && allowedHosts.Contains(host[4..]));

    private static string RedactPatterns(string segment)
    {
        var result = RawCnpjPattern().Replace(segment, IdentifierToken);
        result = FormattedCnpjPattern().Replace(result, IdentifierToken);
        result = RawCpfPattern().Replace(result, IdentifierToken);
        result = FormattedCpfPattern().Replace(result, IdentifierToken);
        result = BankDataPattern().Replace(result, match => match.Groups[1].Value + " " + BankToken);
        result = IbanLikePattern().Replace(result, BankToken);
        result = MedicalMarkerPattern().Replace(result, MedicalToken);
        return EmailPattern().Replace(result, EmailToken);
    }
}
