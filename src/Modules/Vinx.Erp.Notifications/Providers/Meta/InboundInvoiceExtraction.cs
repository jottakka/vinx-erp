using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vinx.Erp.Notifications.Providers.Meta;

public sealed record ExtractedInvoiceData(
    string? BeneficiaryName,
    string? Amount,
    string? DueDate,
    string? DocumentNumber
);

/// <summary>
/// Extração tipada da fatura (#272 PR3): um turno único pelo perfil de texto
/// com contrato JSON estrito. A transcrição e a legenda entram como dado
/// rotulado — nunca como instrução — e campos ausentes permanecem nulos
/// (sem defaults silenciosos, sem arredondamento: o valor viaja como texto).
/// </summary>
public sealed class InboundInvoiceExtractor(IChatClient textClient)
{
    private const string Instruction =
        "You read invoice documents. Reply ONLY with a JSON object with the exact keys "
        + "\"beneficiary_name\", \"amount\", \"due_date\", \"document_number\". "
        + "Values are strings or null. amount uses plain digits with an optional dot "
        + "and up to two decimals (for example 250.00); never add currency symbols, "
        + "thousand separators or signs. due_date uses YYYY-MM-DD. "
        + "Use null for anything not explicitly stated. Do not guess or compute values.";

    public async Task<ExtractedInvoiceData> ExtractAsync(
        string documentText,
        string? caption,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(documentText);

        var userContent = caption is { Length: > 0 }
            ? $"Caption: {caption}\n\nTranscription:\n{documentText}"
            : $"Transcription:\n{documentText}";
        var response = await textClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userContent)],
            cancellationToken: cancellationToken
        );
        return ParseStrict(response.Text) is { } data
            ? data
            : throw new InboundWhatsAppPermanentException("invoice extraction response was invalid");
    }

    internal static ExtractedInvoiceData? ParseStrict(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetPropertyCount() != 4)
            {
                return null;
            }

            return new ExtractedInvoiceData(
                ReadOptionalString(root, "beneficiary_name"),
                ReadOptionalString(root, "amount"),
                ReadOptionalString(root, "due_date"),
                ReadOptionalString(root, "document_number")
            );
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new KeyNotFoundException(name);
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
