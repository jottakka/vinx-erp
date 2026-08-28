using System.Security.Cryptography;
using System.Text;

namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Serialização canônica não ambígua para hashes de importação: cada campo é
/// emoldurado como tag de tipo + comprimento (big-endian) + bytes UTF-8.
/// Delimitadores internos de valor ("|", "\u2205", nova linha) nunca mudam o
/// enquadramento, e <c>null</c> tem tag própria, distinta de qualquer literal.
/// </summary>
internal static class CanonicalDigest
{
    private const byte NullTag = 0x00;
    private const byte StringTag = 0x01;
    private const byte BoolTag = 0x02;
    private const byte LongTag = 0x03;

    internal sealed class Builder : IDisposable
    {
        private readonly MemoryStream _buffer = new();

        internal Builder(string domain) => Field(StringTag, Encoding.UTF8.GetBytes(domain));

        internal Builder WriteNullableString(string? value)
        {
            if (value is null)
            {
                _buffer.WriteByte(NullTag);
            }
            else
            {
                Field(StringTag, Encoding.UTF8.GetBytes(value));
            }

            return this;
        }

        internal Builder WriteBool(bool value)
        {
            _buffer.WriteByte(BoolTag);
            _buffer.WriteByte(value ? (byte)1 : (byte)0);
            return this;
        }

        internal Builder WriteLong(long value)
        {
            _buffer.WriteByte(LongTag);
            _buffer.WriteByte((byte)(value >> 56));
            _buffer.WriteByte((byte)(value >> 48));
            _buffer.WriteByte((byte)(value >> 40));
            _buffer.WriteByte((byte)(value >> 32));
            _buffer.WriteByte((byte)(value >> 24));
            _buffer.WriteByte((byte)(value >> 16));
            _buffer.WriteByte((byte)(value >> 8));
            _buffer.WriteByte((byte)value);
            return this;
        }

        internal string ToSha256Hex() => Convert.ToHexStringLower(SHA256.HashData(_buffer.ToArray()));

        private void Field(byte tag, byte[] payload)
        {
            _buffer.WriteByte(tag);
            _buffer.WriteByte((byte)(payload.Length >> 24));
            _buffer.WriteByte((byte)(payload.Length >> 16));
            _buffer.WriteByte((byte)(payload.Length >> 8));
            _buffer.WriteByte((byte)payload.Length);
            _buffer.Write(payload, 0, payload.Length);
        }

        public void Dispose() => _buffer.Dispose();
    }
}

/// <summary>Resultado por empresa de uma execução em modo escrita.</summary>
public enum CompanyImportOutcome
{
    /// <summary>Lote novo: receipt inserida e staging populado na mesma transação.</summary>
    ImportedNew,

    /// <summary>Replay idempotente: receipt idêntica já existia (mesmo hash e digest).</summary>
    ReplayedSkip,

    /// <summary>Lock consultivo ocupado por outra execução concorrente da mesma empresa.</summary>
    ConcurrentSkip,

    /// <summary>Lote mutado sob o mesmo batch: transação da empresa revertida inteira.</summary>
    ConflictedRollback,

    /// <summary>Dry-run: empresa validada sem qualquer escrita.</summary>
    Validated,
}

internal static class CompanyImportOutcomeLabels
{
    public static string ToLabel(this CompanyImportOutcome outcome) => outcome switch
    {
        CompanyImportOutcome.ImportedNew => "imported_new",
        CompanyImportOutcome.ReplayedSkip => "replayed_skip",
        CompanyImportOutcome.ConcurrentSkip => "concurrent_skip",
        CompanyImportOutcome.ConflictedRollback => "conflicted_rollback",
        CompanyImportOutcome.Validated => "validated",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>Vínculo determinístico entre chave de empresa legada e empresa alvo.</summary>
public sealed record CompanyMapping(
    string CompanyKey,
    Guid LegacyEmpresaId,
    Guid TargetCompanyId);

/// <summary>Resultado consolidado da empresa após uma execução com <c>--apply</c>.</summary>
public sealed record CompanyResult(
    string CompanyKey,
    CompanyImportOutcome Outcome,
    int Rows);

/// <summary>Saída de uma execução em modo escrita.</summary>
public sealed record ApplyResult(bool Succeeded, IReadOnlyList<CompanyResult> Companies);

/// <summary>Problema de validação do dry-run, sem dado de negócio embutido.</summary>
public sealed record ImportValidationProblem(string Code, string Detail);
