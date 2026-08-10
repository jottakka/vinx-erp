namespace Vinx.Erp.Foundation.Files;

public sealed record SignedStorageUrl(Uri Url, DateTimeOffset ExpiresAt);

public sealed record SignedStorageUploadUrl(Uri Url);

public sealed record StorageObjectMetadata(
    string Key,
    long SizeBytes,
    string ContentType,
    string Checksum);

public enum MalwareScanStatus
{
    Unavailable = 0,
    Clean = 1,
    MalwareDetected = 2
}

public sealed record StorageObjectInspection(
    StorageObjectMetadata Metadata,
    MalwareScanStatus MalwareScanStatus);

public sealed record StorageObjectSummary(string Key, DateTimeOffset? CreatedAt);

public interface FileStorage
{
    Task EnsurePrivateContainerAsync(CancellationToken cancellationToken);

    Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
        string providerKey,
        CancellationToken cancellationToken);

    Task<StorageObjectMetadata> GetObjectMetadataAsync(string providerKey, CancellationToken cancellationToken);

    Task<StorageObjectInspection> InspectObjectAsync(string providerKey, CancellationToken cancellationToken);

    Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string prefix,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken);
}
