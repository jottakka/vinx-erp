namespace Vinx.Erp.Foundation.Files;

public sealed record SignedStorageUrl(Uri Url, DateTimeOffset ExpiresAt);

public sealed record StorageObjectMetadata(
    string Key,
    long SizeBytes,
    string ContentType,
    string Checksum,
    DateTimeOffset CreatedAt);

public sealed record StorageObjectSummary(string Key, DateTimeOffset CreatedAt);

public interface FileStorage
{
    Task EnsurePrivateContainerAsync(CancellationToken cancellationToken);

    Task<SignedStorageUrl> CreateSignedUploadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    Task<StorageObjectMetadata> GetObjectMetadataAsync(string providerKey, CancellationToken cancellationToken);

    Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string prefix,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken);
}
