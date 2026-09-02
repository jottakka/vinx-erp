using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Files;

/// <summary>
/// Libera a confirmação somente com veredito antimalware <see cref="MalwareScanStatus.Clean"/> atual:
/// engine/versão/assinatura preenchidas, timestamp não-default e checksum vinculado ao objeto
/// inspecionado agora. Qualquer outro veredito (detectado, indisponível, malformado ou antigo)
/// falha fechado, sem liberar o objeto da quarentena.
/// </summary>
public static class DocumentVerificationPolicy
{
    public static string? Validate(
        FileRecord record,
        StorageObjectInspection inspection,
        MalwareScanVerdict verdict)
    {
        var stored = inspection.Metadata;
        if (!string.Equals(stored.Key, record.ProviderKey, StringComparison.Ordinal))
        {
            return "object_key_mismatch";
        }

        if (stored.SizeBytes != record.SizeBytes)
        {
            return "size_mismatch";
        }

        if (!string.Equals(stored.Checksum, record.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            return "checksum_mismatch";
        }

        if (!string.Equals(record.MimeType, stored.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return "content_type_mismatch";
        }

        if (verdict.Status == MalwareScanStatus.MalwareDetected)
        {
            return "malware_detected";
        }

        if (verdict.Status != MalwareScanStatus.Clean)
        {
            return "malware_scan_unavailable";
        }

        if (string.IsNullOrWhiteSpace(verdict.Engine)
            || string.IsNullOrWhiteSpace(verdict.EngineVersion)
            || string.IsNullOrWhiteSpace(verdict.SignatureVersion)
            || string.IsNullOrWhiteSpace(verdict.ContentChecksum)
            || verdict.ScannedAtUtc == default)
        {
            return "malware_scan_unavailable";
        }

        return !string.Equals(verdict.ContentChecksum, stored.Checksum, StringComparison.OrdinalIgnoreCase)
            ? "malware_scan_unavailable"
            : MatchesMagicBytes(stored.ContentType, inspection.LeadingBytes)
                ? null
                : "content_magic_mismatch";
    }

    /// <summary>
    /// O Content-Type declarado é controlado pelo cliente, então os tipos binários
    /// permitidos precisam bater com os primeiros bytes inspecionados no storage.
    /// text/plain não tem assinatura confiável e fica de fora do sniff.
    /// </summary>
    private static bool MatchesMagicBytes(string contentType, ReadOnlyMemory<byte> leadingBytes)
    {
        ReadOnlySpan<byte> signature = MediaType(contentType) switch
        {
            "application/pdf" => "%PDF-"u8,
            "image/jpeg" => [0xFF, 0xD8, 0xFF],
            "image/png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            _ => default
        };
        return signature.IsEmpty || leadingBytes.Span.StartsWith(signature);
    }

    private static string MediaType(string contentType)
    {
        var separator = contentType.IndexOf(';');
        return (separator < 0 ? contentType : contentType[..separator]).Trim().ToLowerInvariant();
    }
}
