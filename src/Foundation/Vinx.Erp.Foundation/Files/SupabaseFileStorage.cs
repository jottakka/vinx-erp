using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Buffers;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;

namespace Vinx.Erp.Foundation.Files;

public sealed class SupabaseFileStorage(HttpClient httpClient, SupabaseStorageOptions options) : FileStorage
{
    private const int ObjectListPageSize = 1000;
    private static readonly TimeSpan MaximumDownloadUrlLifetime = TimeSpan.FromMinutes(2);

    public async Task EnsurePrivateContainerAsync(CancellationToken cancellationToken)
    {
        options.Validate();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "storage/v1/bucket")
        {
            Content = JsonContent.Create(new CreateBucketRequest(options.ContainerId, options.ContainerId, false))
        };
        AddAuthenticationHeaders(createRequest);

        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        if (createResponse.IsSuccessStatusCode)
        {
            return;
        }

        using var lookupRequest = new HttpRequestMessage(HttpMethod.Get, $"storage/v1/bucket/{options.ContainerId}");
        AddAuthenticationHeaders(lookupRequest);
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
            $"Supabase Storage container provisioning failed with create status {(int)createResponse.StatusCode}, lookup status {(int)lookupResponse.StatusCode}, container {options.ContainerId}.");
    }

    public async Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
        string providerKey,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        options.Validate();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ObjectPath("upload/sign", providerKey))
        {
            Content = JsonContent.Create(new { upsert = false })
        };
        AddAuthenticationHeaders(request);
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

    public async Task<StorageObjectInspection> InspectObjectAsync(string providerKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        options.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Get, ObjectPath(string.Empty, providerKey));
        AddAuthenticationHeaders(request);
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
                    $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}"),
                MalwareScanStatus.Unavailable);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public async Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
        string providerKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        ValidateDownloadLifetime(lifetime);
        options.Validate();

        using var request = new HttpRequestMessage(HttpMethod.Post, ObjectPath("sign", providerKey))
        {
            Content = JsonContent.Create(new { expiresIn = (int)lifetime.TotalSeconds })
        };
        AddAuthenticationHeaders(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var url = await ReadUrlAsync(response.Content, cancellationToken);
        return new SignedStorageUrl(url, DateTimeOffset.UtcNow.Add(lifetime));
    }

    public async Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(prefix, allowEmpty: true);
        options.Validate();
        var offset = 0;
        var summaries = new List<StorageObjectSummary>();
        while (true)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(options.BaseUri, $"storage/v1/object/list/{Uri.EscapeDataString(options.ContainerId)}"))
            {
                Content = JsonContent.Create(new { prefix, limit = ObjectListPageSize, offset })
            };
            AddAuthenticationHeaders(request);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var objects = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken)
                ?? [];
            summaries.AddRange(objects.Select(ToStorageObjectSummary));
            if (objects.Length < ObjectListPageSize)
            {
                return summaries;
            }

            offset = checked(offset + objects.Length);
        }
    }

    public async Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(providerKey);
        options.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectPath(string.Empty, providerKey));
        AddAuthenticationHeaders(request);
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

    private void AddAuthenticationHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceRoleKey);
        request.Headers.Add("apikey", options.ServiceRoleKey);
    }

    private Uri ObjectPath(string operation, string providerKey)
    {
        var encodedKey = string.Join('/', providerKey.Split('/').Select(Uri.EscapeDataString));
        var suffix = string.IsNullOrEmpty(operation) ? string.Empty : $"/{operation}";
        return new Uri(
            options.BaseUri,
            $"storage/v1/object{suffix}/{Uri.EscapeDataString(options.ContainerId)}/{encodedKey}");
    }

    private async Task<Uri> ReadUrlAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var payload = await content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var value = payload.ValueKind == JsonValueKind.Object
            ? ReadFirstString(payload, "signedUrl", "signedURL", "url")
            : null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(new Uri(options.BaseUri, "/"), value, out var uri)
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
            || providerKey.StartsWith("/", StringComparison.Ordinal)
            || providerKey.EndsWith("/", StringComparison.Ordinal)
            || providerKey.Contains('\\'))
        {
            throw new ArgumentException("Storage object key is invalid.", nameof(providerKey));
        }
    }

    private static StorageObjectSummary ToStorageObjectSummary(JsonElement item)
    {
        var name = item.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString()
            : null;
        var createdAt = item.TryGetProperty("created_at", out var createdAtProperty)
            && DateTimeOffset.TryParse(
                createdAtProperty.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? parsed
            : (DateTimeOffset?)null;
        return new StorageObjectSummary(
            string.IsNullOrWhiteSpace(name) ? throw new InvalidOperationException("Storage object name is missing.") : name,
            createdAt);
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
