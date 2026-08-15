using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Files;

namespace Vinx.Erp.Foundation.IntegrationTests.Storage;

public sealed class SupabaseStorageFixture : IAsyncDisposable
{
    public const string ContainerId = "vinx-foundation-private";

    private readonly ServiceProvider _serviceProvider;
    private readonly HttpClient _adminClient;

    private SupabaseStorageFixture(SupabaseStorageOptions options)
    {
        Options = options;
        var services = new ServiceCollection();
        services.AddHttpClient<SupabaseFileStorage>(client =>
        {
            client.BaseAddress = options.BaseUri;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceRoleKey);
            client.DefaultRequestHeaders.Add("apikey", options.ServiceRoleKey);
        });
        services.AddHttpClient("SupabaseStorageAdmin", client =>
        {
            client.BaseAddress = options.BaseUri;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceRoleKey);
            client.DefaultRequestHeaders.Add("apikey", options.ServiceRoleKey);
        });
        services.AddHttpClient("SupabaseStorageAnonymous", client => client.BaseAddress = options.BaseUri);
        _serviceProvider = services.BuildServiceProvider();
        _adminClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("SupabaseStorageAdmin");
    }

    public SupabaseStorageOptions Options { get; }

    public static SupabaseStorageFixture FromEnvironment()
    {
        var baseUri = Environment.GetEnvironmentVariable("SUPABASE_STORAGE_URL");
        var serviceRoleKey = Environment.GetEnvironmentVariable("SUPABASE_STORAGE_SERVICE_ROLE_KEY");
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var parsedBaseUri)
            || string.IsNullOrWhiteSpace(serviceRoleKey))
        {
            throw new InvalidOperationException("Supabase Storage test environment is not configured.");
        }

        return new SupabaseStorageFixture(new SupabaseStorageOptions
        {
            BaseUri = parsedBaseUri,
            ServiceRoleKey = serviceRoleKey
        });
    }

    public SupabaseFileStorage CreateStorage() => _serviceProvider.GetRequiredService<SupabaseFileStorage>();

    public async Task<BucketResponse> GetBucketAsync(CancellationToken cancellationToken)
    {
        using var response = await _adminClient.GetAsync($"storage/v1/bucket/{ContainerId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BucketResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Storage bucket lookup returned no bucket.");
    }

    public async Task UploadFixtureObjectAsync(string objectName, CancellationToken cancellationToken)
    {
        using var content = new StringContent("vinx-storage-fixture", System.Text.Encoding.ASCII, "text/plain");
        using var response = await _adminClient.PostAsync($"storage/v1/object/{ContainerId}/{objectName}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> GetAnonymousObjectAsync(string objectName, CancellationToken cancellationToken)
    {
        var client = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("SupabaseStorageAnonymous");
        return await client.GetAsync($"storage/v1/object/public/{ContainerId}/{objectName}", cancellationToken);
    }

    public async Task DeleteFixtureObjectAsync(string objectName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"storage/v1/object/{ContainerId}/{objectName}");
        using var response = await _adminClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        _serviceProvider.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record BucketResponse(string Id, bool Public);
