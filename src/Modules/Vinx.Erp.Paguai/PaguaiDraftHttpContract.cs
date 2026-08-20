using System.Globalization;
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
        "amount",
        "currency",
        "dueDate",
        "beneficiaryDisplayName",
        "proofRequirement",
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
            || !TryGetAmount(fields["amount"], out var amount)
            || fields["currency"].ValueKind is not JsonValueKind.String
            || fields["dueDate"].ValueKind is not JsonValueKind.String
            || fields["beneficiaryDisplayName"].ValueKind is not JsonValueKind.String
            || fields["proofRequirement"].ValueKind is not JsonValueKind.String
        )
        {
            return false;
        }

        var title = fields["title"].GetString()!;
        var description = fields["description"].GetString()!;
        var currency = fields["currency"].GetString()!;
        var dueDateText = fields["dueDate"].GetString()!;
        var beneficiaryDisplayName = fields["beneficiaryDisplayName"].GetString()!;
        var proofRequirement = fields["proofRequirement"].GetString()!;
        if (
            companyId == Guid.Empty
            || requestId == Guid.Empty
            || !PaguaiAmountRules.IsValid(amount)
            || !IsTrimmedText(title, 1, 200)
            || !IsTrimmedText(description, 1, 4_000)
            || !string.Equals(currency, "BRL", StringComparison.Ordinal)
            || !DateOnly.TryParseExact(
                dueDateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dueDate
            )
            || !IsTrimmedText(beneficiaryDisplayName, 1, 200)
            || (proofRequirement is not "optional" and not "required")
        )
        {
            return false;
        }

        command = new CreateDraftCommand(
            companyId,
            requestId,
            title,
            description,
            amount,
            currency,
            dueDate,
            beneficiaryDisplayName,
            proofRequirement
        );
        return true;
    }

    private static bool IsTrimmedText(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool TryGetAmount(JsonElement value, out decimal amount)
    {
        amount = default;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out amount))
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                value.GetString(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out amount
            );
    }
}
