using System.Globalization;
using System.Text.Json;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.AI.Guardrails;

/// <summary>
/// Converte o envelope fechado da jornada Pagaí em proposta segura. A leitura
/// é puramente efêmera: OCR e JSON bruto nunca atravessam o resultado.
/// </summary>
public static class PaguaiDocumentProposalParser
{
    private const string SchemaInvalid = "schema_invalid";
    private const string LowConfidence = "low_confidence";
    private const string InsufficientEvidence = "insufficient_evidence";
    private const double MinimumConfidence = 0.80;

    private static readonly IReadOnlySet<string> EnvelopeFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "status",
        "reason",
        "proposal",
    };

    private static readonly IReadOnlySet<string> ProposalFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "title",
        "description",
        "amount",
        "currency",
        "dueDate",
        "beneficiaryDisplayName",
        "proofRequirement",
        "invoiceFileId",
        "confidence",
        "sourceReferences",
    };

    private static readonly IReadOnlySet<string> SourceReferenceFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "field",
        "source",
    };

    private static readonly IReadOnlySet<string> RequiredSourceFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "amount",
        "dueDate",
        "beneficiaryDisplayName",
    };

    private static readonly IReadOnlySet<string> ClarificationReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "not_financial_document",
        InsufficientEvidence,
        LowConfidence,
        "schema_invalid",
        "document_context_unavailable",
    };

    /// <summary>Analisa uma única saída estruturada contra o anexo e páginas autorizados.</summary>
    public static PaguaiDocumentProposalParseResult Parse(PaguaiDocumentProposalParseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AuthorizedSourceReferences);

        if (string.IsNullOrWhiteSpace(request.StructuredJson))
        {
            return Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(
                request.StructuredJson,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (!IsClosedObject(root, EnvelopeFields)
                || !TryGetString(root, "status", out var status)
                || !root.TryGetProperty("reason", out var reasonElement)
                || !root.TryGetProperty("proposal", out var proposalElement))
            {
                return Invalid();
            }

            return status switch
            {
                "proposal" when reasonElement.ValueKind is JsonValueKind.Null
                    && proposalElement.ValueKind is JsonValueKind.Object => ParseProposal(proposalElement, request),
                "needs_clarification" when proposalElement.ValueKind is JsonValueKind.Null
                    && TryGetAllowedReason(reasonElement, out var reason) => NeedsClarification(reason),
                _ => Invalid(),
            };
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    private static PaguaiDocumentProposalParseResult ParseProposal(
        JsonElement proposalElement,
        PaguaiDocumentProposalParseRequest request)
    {
        if (!IsClosedObject(proposalElement, ProposalFields)
            || !TryGetString(proposalElement, "title", out var title)
            || !TryGetString(proposalElement, "description", out var description)
            || !TryGetString(proposalElement, "amount", out var amountText)
            || !TryGetString(proposalElement, "currency", out var currency)
            || !TryGetString(proposalElement, "dueDate", out var dueDateText)
            || !TryGetString(proposalElement, "beneficiaryDisplayName", out var beneficiaryDisplayName)
            || !TryGetString(proposalElement, "proofRequirement", out var proofRequirement)
            || !TryGetString(proposalElement, "invoiceFileId", out var invoiceFileIdText)
            || !proposalElement.TryGetProperty("confidence", out var confidenceElement)
            || !proposalElement.TryGetProperty("sourceReferences", out var sourceReferencesElement)
            || !PaguaiAmountRules.TryParseText(amountText, out var amount)
            || !PaguaiAmountRules.IsValid(amount)
            || currency != "BRL"
            || !TryParseCanonicalDate(dueDateText, out var dueDate)
            || proofRequirement is not ("optional" or "required")
            || !Guid.TryParseExact(invoiceFileIdText, "D", out var invoiceFileId)
            || invoiceFileId != request.FileRecordId
            || confidenceElement.ValueKind != JsonValueKind.Number
            || !confidenceElement.TryGetDouble(out var confidence)
            || !double.IsFinite(confidence)
            || confidence is < 0 or > 1)
        {
            return Invalid();
        }

        if (!TryParseSources(sourceReferencesElement, request.AuthorizedSourceReferences, out var sourceReferences))
        {
            return NeedsClarification(InsufficientEvidence);
        }

        if (confidence < MinimumConfidence)
        {
            return NeedsClarification(LowConfidence);
        }

        return new PaguaiDocumentProposalParseResult(
            PaguaiProposalParseStatus.Proposal,
            Reason: null,
            new PaguaiDocumentProposal(
                title,
                description,
                PaguaiAmountRules.Canonicalize(amount),
                currency,
                dueDate,
                beneficiaryDisplayName,
                proofRequirement,
                invoiceFileId,
                confidence,
                sourceReferences));
    }

    private static bool TryParseSources(
        JsonElement sourceReferencesElement,
        IReadOnlySet<string> authorizedSourceReferences,
        out IReadOnlyList<PaguaiProposalSourceReference> sourceReferences)
    {
        sourceReferences = [];
        if (sourceReferencesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<PaguaiProposalSourceReference>();
        var coveredFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in sourceReferencesElement.EnumerateArray())
        {
            if (!IsClosedObject(element, SourceReferenceFields)
                || !TryGetString(element, "field", out var field)
                || !TryGetString(element, "source", out var source)
                || !RequiredSourceFields.Contains(field)
                || !authorizedSourceReferences.Contains(source))
            {
                return false;
            }

            parsed.Add(new PaguaiProposalSourceReference(field, source));
            coveredFields.Add(field);
        }

        if (!coveredFields.SetEquals(RequiredSourceFields))
        {
            return false;
        }

        sourceReferences = parsed;
        return true;
    }

    private static bool IsClosedObject(JsonElement element, IReadOnlySet<string> allowedFields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = element.EnumerateObject().ToArray();
        return properties.Length == allowedFields.Count
            && properties.All(property => allowedFields.Contains(property.Name))
            && properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == allowedFields.Count;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty) is not null;
    }

    private static bool TryGetAllowedReason(JsonElement element, out string reason)
    {
        reason = string.Empty;
        return element.ValueKind == JsonValueKind.String
            && (reason = element.GetString() ?? string.Empty) is not null
            && ClarificationReasons.Contains(reason);
    }

    private static bool TryParseCanonicalDate(string value, out DateOnly dueDate) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dueDate)
        && dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == value;

    private static PaguaiDocumentProposalParseResult Invalid() =>
        new(PaguaiProposalParseStatus.Invalid, SchemaInvalid, Proposal: null);

    private static PaguaiDocumentProposalParseResult NeedsClarification(string reason) =>
        new(PaguaiProposalParseStatus.NeedsClarification, reason, Proposal: null);
}
