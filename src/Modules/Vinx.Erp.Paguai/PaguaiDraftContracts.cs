using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vinx.Erp.Paguai;

public sealed record CreateDraftCommand(
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement,
    Guid? SourceInboundMessageId = null
);

/// <summary>
/// Comando de rascunho a partir de um documento inbound: title/description/proof
/// são sempre explícitos porque a fatura não os garante.
/// </summary>
public sealed record CreateDraftFromDocumentCommand(
    Guid CompanyId,
    Guid InboundMessageId,
    string Title,
    string Description,
    string ProofRequirement
);

public sealed record PaguaiDraft(
    Guid Id,
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description,
    [property: JsonConverter(typeof(PaguaiAmountJsonConverter))]
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement,
    string State,
    int Version,
    DateTimeOffset CreatedAt
);

/// <summary>Transporta valores Paguai como texto decimal exato; número JSON é rejeitado.</summary>
public sealed class PaguaiAmountJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (
            reader.TokenType == JsonTokenType.String
            && PaguaiAmountRules.TryParseText(reader.GetString(), out var amount)
        )
        {
            return amount;
        }

        throw new JsonException("Paguai amount must be an exact decimal string.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        decimal value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value.ToString("0.00", CultureInfo.InvariantCulture));
}

/// <summary>Define o intervalo e a escala monetária aceitos pelo Paguai.</summary>
public static class PaguaiAmountRules
{
    public const decimal Minimum = 0.01m;
    public const decimal Maximum = 9_999_999_999_999_999.99m;

    // Aceita qualquer escala de dígitos — recibos convertidos pela migration
    // podem trazer zeros além da segunda casa; a validação de escala do
    // comando continua em IsValid.
    private static readonly Regex TextGrammar = new(
        @"^\d+(?:\.\d+)?$",
        RegexOptions.Compiled
    );

    /// <summary>Analisa somente o texto decimal do contrato; rejeita "1250.", ".5" e expoentes.</summary>
    public static bool TryParseText(string? candidate, out decimal amount)
    {
        amount = default;
        return candidate is not null
            && TextGrammar.IsMatch(candidate)
            && decimal.TryParse(
                candidate,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out amount
            );
    }

    /// <summary>Normaliza um valor válido para duas casas antes do hash e da persistência.</summary>
    public static decimal Canonicalize(decimal amount) => decimal.Parse(
        amount.ToString("0.00", CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture
    );

    public static bool IsValid(decimal amount) =>
        amount >= Minimum
        && amount <= Maximum
        && ((decimal.GetBits(amount)[3] >> 16) & 0x7F) <= 2;
}

public enum PaguaiDraftState
{
    Created,
    Replay,
    Invalid,
    Forbidden,
    NotFound,
    IdempotencyConflict,
    Unavailable,
}

public sealed record PaguaiDraftResult(PaguaiDraftState State, PaguaiDraft? Draft = null)
{
    public static PaguaiDraftResult Of(PaguaiDraftState state) => new(state);
}

public sealed record PaguaiDraftListResult(
    PaguaiDraftState State,
    IReadOnlyList<PaguaiDraft>? Drafts = null
)
{
    public static PaguaiDraftListResult Of(PaguaiDraftState state) => new(state);
}
