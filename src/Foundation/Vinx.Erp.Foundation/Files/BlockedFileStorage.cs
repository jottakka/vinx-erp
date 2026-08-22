namespace Vinx.Erp.Foundation.Files;

/// <summary>
/// Storage fail-closed para hosts que sobem sem a configuração do Supabase
/// Storage (padrão do BlockedMalwareScanner): nenhuma operação lê, escreve
/// ou confirma objeto — o caller decide o motivo estável de falha.
/// </summary>
public sealed class BlockedFileStorage : FileStorage
{
    public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) =>
        Unavailable<Task>();

    public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
        string providerKey,
        CancellationToken cancellationToken
    ) => Unavailable<Task<SignedStorageUploadUrl>>();

    public Task UploadObjectAsync(
        string providerKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken
    ) => Unavailable<Task>();

    public Task<StorageObjectMetadata> GetObjectMetadataAsync(
        string providerKey,
        CancellationToken cancellationToken
    ) => Unavailable<Task<StorageObjectMetadata>>();

    public Task<StorageObjectInspection> InspectObjectAsync(
        string providerKey,
        CancellationToken cancellationToken
    ) => Unavailable<Task<StorageObjectInspection>>();

    public Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken) =>
        Unavailable<Task<Stream>>();

    public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken
    ) => Unavailable<Task<SignedStorageUrl>>();

    public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string prefix,
        CancellationToken cancellationToken
    ) => Unavailable<Task<IReadOnlyList<StorageObjectSummary>>>();

    public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) =>
        Unavailable<Task>();

    private static T Unavailable<T>() =>
        throw new InvalidOperationException("File storage is not configured.");
}
