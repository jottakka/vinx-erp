namespace Vinx.Erp.Foundation.Files;

public static class DocumentUploadPolicy
{
    public const long MaxSizeBytes = 25 * 1024 * 1024;

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

        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "mime_required";
        }

        if (sizeBytes <= 0)
        {
            return "empty_file";
        }

        if (sizeBytes > MaxSizeBytes)
        {
            return "file_too_large";
        }

        if (!IsSha256Checksum(checksum))
        {
            return "checksum_invalid";
        }

        var extension = Path.GetExtension(fileName);
        return !MimeExtensions.TryGetValue(mimeType, out var allowedExtensions)
            || !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? "mime_extension_mismatch"
            : null;
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
