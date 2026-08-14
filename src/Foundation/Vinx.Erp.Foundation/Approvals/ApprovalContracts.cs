using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vinx.Erp.Foundation.Approvals;

/// <summary>
/// Serialização canônica e hash de payload de aprovação. Regra única usada no
/// preparo e no re-hash da confirmação: chaves de objeto ordenadas
/// recursivamente (ordinal), valores preservados e formatação invariante de
/// cultura (a representação numérica crua do JSON já é invariante). O hash é
/// SHA-256 em hexadecimal minúsculo (64 caracteres).
/// </summary>
public static class CanonicalPayloadHasher
{
    public const int HashLength = 64;

    public static string Canonicalize(JsonElement payload)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, payload);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string ComputeHash(JsonElement payload)
    {
        var canonical = Canonicalize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static bool IsValidHash(string? hash)
    {
        if (hash is not { Length: HashLength })
        {
            return false;
        }

        foreach (var character in hash)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("Unsupported payload value kind.", nameof(element));
        }
    }
}

/// <summary>Comando de preparação de uma aprovação durável (escopo PREPARATION).</summary>
public sealed record PrepareApprovalCommand
{
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumTimeToLive = TimeSpan.FromHours(24);

    public PrepareApprovalCommand(
        Guid companyId,
        Guid requestId,
        string action,
        JsonElement payload,
        string policyVersion,
        TimeSpan? timeToLive = null)
    {
        if (companyId == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Company and request identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The approval payload must be a JSON object.");
        }

        var resolvedTimeToLive = timeToLive ?? DefaultTimeToLive;
        if (resolvedTimeToLive <= TimeSpan.Zero || resolvedTimeToLive > MaximumTimeToLive)
        {
            throw new ArgumentException("The approval time-to-live is out of bounds.");
        }

        CompanyId = companyId;
        RequestId = requestId;
        Action = action;
        Payload = payload;
        PolicyVersion = policyVersion;
        TimeToLive = resolvedTimeToLive;
    }

    public Guid CompanyId { get; }
    public Guid RequestId { get; }
    public string Action { get; }
    public JsonElement Payload { get; }

    /// <summary>
    /// Versão da política pertencente ao módulo chamador (o Foundation a trata
    /// como valor opaco); vinculada à aprovação no preparo e revalidada na
    /// confirmação.
    /// </summary>
    public string PolicyVersion { get; }

    public TimeSpan TimeToLive { get; }
}

/// <summary>Resultado da preparação: identifica a proposta opaca viva.</summary>
public sealed record PrepareApprovalResult(
    Guid ApprovalId,
    Guid ProposalId,
    DateTimeOffset ExpiresAt);

/// <summary>Comando de confirmação (escopo CONFIRMATION) com payload canônico re-hashado.</summary>
public sealed record ConfirmApprovalCommand
{
    public ConfirmApprovalCommand(
        Guid companyId,
        Guid approvalId,
        Guid proposalId,
        Guid requestId,
        JsonElement payload,
        string policyVersion)
    {
        if (companyId == Guid.Empty || approvalId == Guid.Empty || proposalId == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Confirmation identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The confirmation payload must be a JSON object.");
        }

        CompanyId = companyId;
        ApprovalId = approvalId;
        ProposalId = proposalId;
        RequestId = requestId;
        Payload = payload;
        PolicyVersion = policyVersion;
    }

    public Guid CompanyId { get; }
    public Guid ApprovalId { get; }
    public Guid ProposalId { get; }
    public Guid RequestId { get; }
    public JsonElement Payload { get; }

    /// <summary>Versão corrente da política do módulo chamador; divergência com a aprovação armazenada é conflito.</summary>
    public string PolicyVersion { get; }
}

/// <summary>Comando de veto (estado terminal auditado).</summary>
public sealed record VetoApprovalCommand
{
    public VetoApprovalCommand(Guid companyId, Guid approvalId, Guid proposalId, Guid requestId)
    {
        CompanyId = RequireNonEmpty(companyId, nameof(companyId));
        ApprovalId = RequireNonEmpty(approvalId, nameof(approvalId));
        ProposalId = RequireNonEmpty(proposalId, nameof(proposalId));
        RequestId = RequireNonEmpty(requestId, nameof(requestId));
    }

    public Guid CompanyId { get; }
    public Guid ApprovalId { get; }
    public Guid ProposalId { get; }
    public Guid RequestId { get; }

    private static Guid RequireNonEmpty(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier must not be empty.", name) : value;
}

/// <summary>Desfechos distintos da confirmação (escopo CONFIRMATION).</summary>
public enum ConfirmApprovalOutcome
{
    /// <summary>Este RequestId venceu o claim single-use e a execução pode prosseguir.</summary>
    Claimed,

    /// <summary>Replay do mesmo RequestId vencedor; retorna o resultado original.</summary>
    Replay,

    /// <summary>Aprovação consumida por outro RequestId ou divergência de payload/política.</summary>
    Conflict,

    /// <summary>Proposta indisponível: desconhecida, expirada, vetada ou perdida por reinício.</summary>
    ProposalUnavailable,
}

/// <summary>
/// Resultado da confirmação. Carrega o <see cref="ProposalId"/> opaco de volta
/// (ERIC-4) para a fase de execução validar a proposta viva.
/// </summary>
public sealed record ConfirmApprovalResult(
    Guid ApprovalId,
    Guid ProposalId,
    ConfirmApprovalOutcome Outcome);

/// <summary>Resultado do veto.</summary>
public sealed record VetoApprovalResult(Guid ApprovalId, Guid ProposalId, bool WasVetoed);
