namespace Vinx.Erp.Foundation.Files;

public static class DocumentUploadPolicy
{
    public const long MaxSizeBytes = 25 * 1024 * 1024;
    public const int MaxFileNameLength = 255;
    public const int MaxExtensionLength = 20;
    public const int MaxMimeTypeLength = 100;
    public const int MaxChecksumLength = 128;
    public const int MaxClassificationLength = 100;
    public const int MaxOriginLength = 100;

    private static readonly IReadOnlyDictionary<string, string[]> MimeExtensions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = [".pdf"],
            ["image/jpeg"] = [".jpg", ".jpeg"],
            ["image/png"] = [".png"],
            ["text/plain"] = [".txt"]
        };

    public static string? Validate(string? fileName, string? mimeType, long sizeBytes, string? checksum)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains('\0'))
        {
            return "file_name_required";
        }

        if (fileName.Length > MaxFileNameLength)
        {
            return "file_name_too_long";
        }

        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "mime_required";
        }

        if (mimeType.Length > MaxMimeTypeLength)
        {
            return "mime_type_too_long";
        }

        if (sizeBytes <= 0)
        {
            return "empty_file";
        }

        if (sizeBytes > MaxSizeBytes)
        {
            return "file_too_large";
        }

        if (checksum?.Length > MaxChecksumLength)
        {
            return "checksum_too_long";
        }

        if (!IsSha256Checksum(checksum))
        {
            return "checksum_invalid";
        }

        var extension = Path.GetExtension(fileName);
        if (extension.Length > MaxExtensionLength)
        {
            return "extension_too_long";
        }

        return !MimeExtensions.TryGetValue(mimeType, out var allowedExtensions)
            || !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? "mime_extension_mismatch"
            : null;
    }

    public static string? ValidateIntentMetadata(
        string? classification,
        string? origin,
        DateTimeOffset retentionUntil)
    {
        if (string.IsNullOrWhiteSpace(classification))
        {
            return "classification_required";
        }

        if (classification.Length > MaxClassificationLength)
        {
            return "classification_too_long";
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            return "origin_required";
        }

        if (origin.Length > MaxOriginLength)
        {
            return "origin_too_long";
        }

        return retentionUntil <= DateTimeOffset.UtcNow ? "retention_invalid" : null;
    }

    private static bool IsSha256Checksum(string? checksum)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(checksum)
            || !checksum.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || checksum.Length != prefix.Length + 64)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(checksum.AsSpan(prefix.Length)).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
