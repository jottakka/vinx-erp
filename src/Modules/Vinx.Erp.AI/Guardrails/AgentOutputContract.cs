using System.Text.Json;

namespace Vinx.Erp.AI.Guardrails;

/// <summary>
/// Resposta normalizada do modelo consumida pelo gate (construída fora daqui
/// a partir da ChatResponse); nunca prompt, contexto ou segredo.
/// </summary>
public sealed record AgentModelResponse(
    string? Text,
    string? StructuredJson,
    IReadOnlyList<AgentCitedSource> CitedSources,
    double? Confidence,
    bool HasToolProposal);

/// <summary>Fonte citada com escopo de empresa; referência opaca controlada pelo servidor.</summary>
public sealed record AgentCitedSource(string Reference, Guid CompanyId);

/// <summary>Desfecho da validação do contrato: válido ou reason code estável.</summary>
public sealed record AgentContractValidation(bool IsValid, string ReasonCode);

/// <summary>
/// Contrato estrito do payload estruturado por jornada. Valida manualmente as
/// propriedades do objeto raiz: campo obrigatório ausente ou campo
/// desconhecido reprova; JSON malformado, raiz não-objeto e null com
/// obrigatórios falham fechado. Conteúdo aninhado não é inspecionado.
/// </summary>
public sealed class AgentOutputContract(string journey, IReadOnlySet<string> requiredFields)
{
    private const string ContractValid = "contract_valid";
    private const string StructuredJsonInvalid = "structured_json_invalid";

    /// <summary>Jornada à qual o contrato se aplica.</summary>
    public string Journey { get; } = journey ?? throw new ArgumentNullException(nameof(journey));

    private readonly IReadOnlySet<string> requiredFields = requiredFields
        ?? throw new ArgumentNullException(nameof(requiredFields));

    /// <summary>Valida o JSON estruturado contra o contrato com reason code estável.</summary>
    public AgentContractValidation Validate(string? structuredJson)
    {
        if (structuredJson is null)
        {
            return NullPayload();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(structuredJson);
        }
        catch (JsonException)
        {
            return Invalid(StructuredJsonInvalid);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return NullPayload();
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(StructuredJsonInvalid);
            }

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!requiredFields.Contains(property.Name))
                {
                    return Invalid($"unknown_field:{property.Name}");
                }

                present.Add(property.Name);
            }

            foreach (var required in requiredFields)
            {
                if (!present.Contains(required))
                {
                    return Invalid($"required_field_missing:{required}");
                }
            }

            return Valid();
        }
    }

    private AgentContractValidation NullPayload() =>
        requiredFields.Count == 0 ? Valid() : Invalid(StructuredJsonInvalid);

    private static AgentContractValidation Valid() => new(true, ContractValid);

    private static AgentContractValidation Invalid(string reasonCode) => new(false, reasonCode);
}
