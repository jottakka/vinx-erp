using System.Text.Json;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Paguai;

/// <summary>Marcador de antiforgery exigido na operação (mantém o contrato OpenAPI e o middleware).</summary>
public sealed class PaguaiAntiforgeryRequiredMetadata : Attribute, IAntiforgeryRequiredMetadata
{
    /// <summary>Bearer dispensa o token; somente a sessão de cookie BFF o exige.</summary>
    public bool CookieAuthenticatedOnly => true;
}

public static class PaguaiDraftHttpContract
{
    private static readonly HashSet<string> Fields =
    [
        "companyId",
        "requestId",
        "title",
        "description",
    ];

    public static bool TryParse(JsonElement body, out CreateDraftCommand command)
    {
        command = null!;
        if (body.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!Fields.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (
            fields.Count != Fields.Count
            || !fields["companyId"].TryGetGuid(out var companyId)
            || !fields["requestId"].TryGetGuid(out var requestId)
            || fields["title"].ValueKind is not JsonValueKind.String
            || fields["description"].ValueKind is not JsonValueKind.String
        )
        {
            return false;
        }

        var title = fields["title"].GetString()!;
        var description = fields["description"].GetString()!;
        if (
            companyId == Guid.Empty
            || requestId == Guid.Empty
            || !IsTrimmedText(title, 1, 200)
            || !IsTrimmedText(description, 1, 4_000)
        )
        {
            return false;
        }

        command = new CreateDraftCommand(companyId, requestId, title, description);
        return true;
    }

    private static bool IsTrimmedText(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
