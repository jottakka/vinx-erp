using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Files;

public static class DocumentVerificationPolicy
{
    public static string? Validate(
        FileRecord record,
        StorageObjectInspection inspection)
    {
        var stored = inspection.Metadata;
        if (!string.Equals(stored.Key, record.ProviderKey, StringComparison.Ordinal))
        {
            return "object_key_mismatch";
        }

        switch (inspection.MalwareScanStatus)
        {
            case MalwareScanStatus.Clean:
                break;
            case MalwareScanStatus.MalwareDetected:
                return "malware_detected";
            default:
                return "malware_scan_unavailable";
        }

        if (stored.SizeBytes != record.SizeBytes)
        {
            return "size_mismatch";
        }

        if (!string.Equals(stored.Checksum, record.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            return "checksum_mismatch";
        }

        return !string.Equals(record.MimeType, stored.ContentType, StringComparison.OrdinalIgnoreCase)
            ? "content_type_mismatch"
            : null;
    }
}
