using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Buffers;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;

namespace Vinx.Erp.Foundation.Files;

public sealed class SupabaseFileStorage(HttpClient httpClient) : FileStorage
{
    private static readonly TimeSpan MaximumDownloadUrlLifetime = TimeSpan.FromMinutes(2);

    public async Task EnsurePrivateContainerAsync(CancellationToken cancellationToken)
    {
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "storage/v1/bucket")
        {
            Content = JsonContent.Create(new CreateBucketRequest(
                SupabaseStorageOptions.PrivateContainerId,
                SupabaseStorageOptions.PrivateContainerId,
                false))
        };

        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        if (createResponse.IsSuccessStatusCode)
        {
            return;
        }

        using var lookupRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"storage/v1/bucket/{SupabaseStorageOptions.PrivateContainerId}");
        using var lookupResponse = await httpClient.SendAsync(lookupRequest, cancellationToken);
        if (lookupResponse.IsSuccessStatusCode)
        {
            var bucket = await ReadBucketAsync(lookupResponse.Content, cancellationToken);
            if (bucket is { Public: false })
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Supabase Storage container provisioning failed with create status {(int)createResponse.StatusCode}, lookup status {(int)lookupResponse.StatusCode}, container {SupabaseStorageOptions.PrivateContainerId}.");
    }

    public async Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
        string providerKey,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ObjectPath("upload/sign", providerKey))
        {
            Content = JsonContent.Create(new { upsert = false })
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var url = await ReadUrlAsync(response.Content, cancellationToken);
        return new SignedStorageUploadUrl(url);
    }

    public async Task<StorageObjectMetadata> GetObjectMetadataAsync(string providerKey, CancellationToken cancellationToken)
    {
        var inspection = await InspectObjectAsync(providerKey, cancellationToken);
        return inspection.Metadata;
    }

    public async Task UploadObjectAsync(
        string providerKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        if (content.Length <= 0 || content.Length > DocumentUploadPolicy.MaxSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("Content type is required.", nameof(contentType));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ObjectPath(string.Empty, providerKey))
        {
            Content = new ReadOnlyMemoryContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Add("x-upsert", "false");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<StorageObjectInspection> InspectObjectAsync(string providerKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, ObjectPath(string.Empty, providerKey));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (response.Content.Headers.ContentLength is long declaredLength
            && declaredLength > DocumentUploadPolicy.MaxSizeBytes)
        {
            throw new InvalidOperationException("Storage object exceeds the maximum inspection size.");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long sizeBytes = 0;
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                sizeBytes = checked(sizeBytes + read);
                if (sizeBytes > DocumentUploadPolicy.MaxSizeBytes)
                {
                    throw new InvalidOperationException("Storage object exceeds the maximum inspection size.");
                }

                hash.AppendData(buffer, 0, read);
            }

            return new StorageObjectInspection(
                new StorageObjectMetadata(
                    providerKey,
                    sizeBytes,
                    contentType,
                    $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Abre o conteúdo privado sem buffer: o GET usa ResponseHeadersRead e o stream é
    /// devolvido direto (o caller dispõe). Sem limite de bytes aqui — o limite é do scanner,
    /// durante o streaming. A response de sucesso não é disposta (seu dispose fecharia o
    /// stream); a de erro — ou cuja materialização do stream falha — é disposta antes do
    /// throw para devolver a conexão ao pool.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, ObjectPath(string.Empty, providerKey));
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            response.EnsureSuccessStatusCode();
        }

        try
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch
        {
            // Sem stream devolvido, a response precisa voltar ao pool.
            response.Dispose();
            throw;
        }
    }

    public async Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        ValidateDownloadLifetime(lifetime);

        using var request = new HttpRequestMessage(HttpMethod.Post, ObjectPath("sign", providerKey))
        {
            Content = JsonContent.Create(new { expiresIn = (int)lifetime.TotalSeconds })
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var url = await ReadUrlAsync(response.Content, cancellationToken);
        return new SignedStorageUrl(url, DateTimeOffset.UtcNow.Add(lifetime));
    }

    public async Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectPath(string.Empty, providerKey));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<BucketResponse?> ReadBucketAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync<BucketResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Uri ObjectPath(string operation, string providerKey)
    {
        var encodedKey = string.Join('/', providerKey.Split('/').Select(Uri.EscapeDataString));
        var suffix = string.IsNullOrEmpty(operation) ? string.Empty : $"/{operation}";
        return new Uri(
            httpClient.BaseAddress!,
            $"storage/v1/object{suffix}/{Uri.EscapeDataString(SupabaseStorageOptions.PrivateContainerId)}/{encodedKey}");
    }

    private async Task<Uri> ReadUrlAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var payload = await content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var value = payload.ValueKind == JsonValueKind.Object
            ? ReadFirstString(payload, "signedUrl", "signedURL", "url")
            : null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(new Uri(httpClient.BaseAddress!, "/"), value, out var uri)
            || !uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Supabase Storage signed URL response was invalid.");
        }

        return uri;
    }

    private static string? ReadFirstString(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static void ValidateObjectKey(string providerKey, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(providerKey))
            || providerKey.Contains("..", StringComparison.Ordinal)
            || providerKey.StartsWith('/')
            || providerKey.EndsWith('/')
            || providerKey.Contains('\\'))
        {
            throw new ArgumentException("Storage object key is invalid.", nameof(providerKey));
        }
    }

    private static void ValidateDownloadLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumDownloadUrlLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
    }

    private sealed record CreateBucketRequest(string Id, string Name, bool Public);
    private sealed record BucketResponse(bool Public);
}
