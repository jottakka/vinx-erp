using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Vinx.Erp.Foundation.Files;

public sealed class SupabaseFileStorage(HttpClient httpClient, SupabaseStorageOptions options) : FileStorage
{
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
            var bucket = await lookupResponse.Content.ReadFromJsonAsync<BucketResponse>(cancellationToken: cancellationToken);
            if (bucket is { Public: false })
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Supabase Storage container provisioning failed with create status {(int)createResponse.StatusCode}, lookup status {(int)lookupResponse.StatusCode}, container {options.ContainerId}.");
    }

    private void AddAuthenticationHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceRoleKey);
        request.Headers.Add("apikey", options.ServiceRoleKey);
    }

    private sealed record CreateBucketRequest(string Id, string Name, bool Public);
    private sealed record BucketResponse(bool Public);
}
