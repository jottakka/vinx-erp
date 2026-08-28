using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace Vinx.Erp.Foundation.Notifications;

public sealed record NotificationInboxAction(string Kind, string Href);

public sealed record NotificationInboxItem(
    Guid Id,
    Guid CompanyId,
    string Source,
    string? Event,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    NotificationInboxAction? Action
);

public sealed record NotificationInboxPage(
    IReadOnlyList<NotificationInboxItem> Items,
    string? NextCursor
);

public enum NotificationInboxState
{
    Listed,
    Invalid,
    Unauthorized,
    Unavailable,
}

public enum NotificationUnreadCountState
{
    Counted,
    Unauthorized,
    Unavailable,
}

public enum NotificationMarkReadState
{
    Read,
    NotFound,
    Invalid,
    Unauthorized,
    Unavailable,
}

public sealed record NotificationInboxResult(
    NotificationInboxState State,
    NotificationInboxPage? Page = null
);

public sealed record NotificationUnreadCountResult(
    NotificationUnreadCountState State,
    long UnreadCount = 0
);

public sealed record NotificationMarkReadResult(NotificationMarkReadState State);

/// <summary>
/// Cursor opaco de paginação keyset: base64url canônico sem padding do ASCII
/// `v1.&lt;micros desde epoch&gt;.&lt;uuid minúsculo&gt;`. Não é segredo nem
/// autoridade; qualquer desvio canônico é rejeitado no decode.
/// </summary>
public static partial class NotificationInboxCursor
{
    public const int MaximumTokenBytes = 128;
    private static readonly Regex CanonicalFormat = CreateCanonicalFormatRegex();

    public static string Encode(DateTimeOffset createdAt, Guid id) =>
        ToBase64UrlString(
            Encoding.ASCII.GetBytes(
                $"v1.{(createdAt.UtcTicks - EpochTicks) / TimeSpan.TicksPerMicrosecond}.{id}"
            )
        );

    public static bool TryDecode(string? token, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenBytes)
        {
            return false;
        }

        var payload = TryDecodeBase64Url(token);
        if (payload is null)
        {
            return false;
        }

        var canonical = Encoding.ASCII.GetString(payload);
        var match = CanonicalFormat.Match(canonical);
        if (
            !match.Success
            || !long.TryParse(match.Groups[1].ValueSpan, out var micros)
            || micros < 0
            || micros > (DateTimeOffset.MaxValue.UtcTicks - EpochTicks) / TimeSpan.TicksPerMicrosecond
            || !Guid.TryParseExact(match.Groups[2].ValueSpan, "D", out id)
        )
        {
            return false;
        }

        createdAt = new DateTimeOffset(EpochTicks + micros * TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        return string.Equals(Encode(createdAt, id), token, StringComparison.Ordinal);
    }

    private static readonly long EpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

    private static string ToBase64UrlString(byte[] payload) =>
        Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? TryDecodeBase64Url(string token)
    {
        var base64 = token.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
            default:
                if (base64.Length % 4 != 0)
                {
                    return null;
                }

                break;
        }

        return Base64.IsValid(base64) ? Convert.FromBase64String(base64) : null;
    }

    [GeneratedRegex("^v1\\.([0-9]{1,19})\\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex CreateCanonicalFormatRegex();
}

/// <summary>
/// Allowlist fechada de apresentação da inbox: chaves completas de evento e
/// seletores completos de deep-link mapeiam para rótulos e ações conhecidos;
/// qualquer chave desconhecida vira source "Sistema" sem ecoar o valor bruto.
/// </summary>
public static partial class NotificationInboxPresentation
{
    public const string DefaultSource = "Sistema";
    private const string PaguaiSource = "Pagaí";
    private const string PaguaiDraftCreatedEvent = "Novo rascunho para aprovação";

    public static readonly IReadOnlyDictionary<string, (string Kind, string Href)> DeepLinks =
        new Dictionary<string, (string Kind, string Href)>
        {
            ["/paguai"] = ("paguai", "/paguai"),
            ["/atendimento"] = ("atendimento", "/atendimento"),
        };

    public static NotificationInboxItem Project(
        Guid id,
        Guid companyId,
        string eventKey,
        string? deepLinkSelector,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt
    )
    {
        var (source, eventName) = PaguaiDraftCreatedEventKeyRegex().IsMatch(eventKey)
            ? (PaguaiSource, PaguaiDraftCreatedEvent)
            : (DefaultSource, null);
        var action =
            deepLinkSelector is not null
            && DeepLinks.TryGetValue(deepLinkSelector, out var mapped)
                ? new NotificationInboxAction(mapped.Kind, mapped.Href)
                : null;
        return new NotificationInboxItem(id, companyId, source, eventName, createdAt, readAt, action);
    }

    [GeneratedRegex("^paguai\\.[0-9a-f]{32}\\.draft-created\\z")]
    private static partial Regex PaguaiDraftCreatedEventKeyRegex();
}
