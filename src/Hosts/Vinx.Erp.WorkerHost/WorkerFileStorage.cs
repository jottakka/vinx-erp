using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Files;

namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Composição de storage do worker para dependência inline-ável do codegen do
/// Wolverine (classe pública tipada, sem factory lambda): sem configuração do
/// Supabase Storage permanece fail-closed com BlockedFileStorage.
/// </summary>
public sealed class WorkerFileStorage(
    SupabaseFileStorage supabase,
    IOptions<SupabaseStorageOptions> options
) : FileStorage
{
    private readonly FileStorage _storage =
        string.IsNullOrWhiteSpace(options.Value.ServiceRoleKey)
            ? new BlockedFileStorage()
            : supabase;

    public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) =>
        _storage.EnsurePrivateContainerAsync(cancellationToken);

    public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
        string providerKey,
        CancellationToken cancellationToken
    ) => _storage.CreateSignedUploadUrlAsync(providerKey, cancellationToken);

    public Task UploadObjectAsync(
        string providerKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken
    ) => _storage.UploadObjectAsync(providerKey, content, contentType, cancellationToken);

    public Task<StorageObjectMetadata> GetObjectMetadataAsync(
        string providerKey,
        CancellationToken cancellationToken
    ) => _storage.GetObjectMetadataAsync(providerKey, cancellationToken);

    public Task<StorageObjectInspection> InspectObjectAsync(
        string providerKey,
        CancellationToken cancellationToken
    ) => _storage.InspectObjectAsync(providerKey, cancellationToken);

    public Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken) =>
        _storage.OpenReadAsync(providerKey, cancellationToken);

    public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken
    ) => _storage.CreateSignedDownloadUrlAsync(providerKey, lifetime, cancellationToken);

    public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) =>
        _storage.DeleteObjectAsync(providerKey, cancellationToken);
}
