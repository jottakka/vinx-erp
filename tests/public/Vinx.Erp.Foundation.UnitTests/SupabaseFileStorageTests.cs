using System.Net;
using System.Text;
using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class SupabaseFileStorageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("provider-body-parser-diagnostic")]
    public async Task Duplicate_create_with_invalid_lookup_json_throws_only_sanitized_statuses_and_container(
        string responseBody)
    {
        using var handler = new SequencedResponseHandler(
            new HttpResponseMessage(HttpStatusCode.Conflict),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = new SupabaseFileStorage(
            client,
            new SupabaseStorageOptions(
                client.BaseAddress,
                "not-a-credential",
                SupabaseStorageOptions.PrivateContainerId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.EnsurePrivateContainerAsync(CancellationToken.None));

        Assert.Equal(
            "Supabase Storage container provisioning failed with create status 409, lookup status 200, container vinx-foundation-private.",
            exception.Message);
    }

    [Fact]
    public async Task Signed_urls_and_object_operations_use_the_private_container()
    {
        using var handler = new RequestSequenceHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "/storage/v1/object/upload/sign/vinx-foundation-private/quarantine/company/document",
                    request.RequestUri!.AbsolutePath);
                return JsonResponse("{\"signedURL\":\"/signed-upload\"}");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "/storage/v1/object/sign/vinx-foundation-private/quarantine/company/document",
                    request.RequestUri!.AbsolutePath);
                return JsonResponse("{\"signedUrl\":\"/signed-download\"}");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/storage/v1/object/list/vinx-foundation-private", request.RequestUri!.AbsolutePath);
                return JsonResponse("[{\"name\":\"quarantine/company/document\",\"created_at\":\"2026-08-10T00:00:00Z\"}]");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal(
                    "/storage/v1/object/vinx-foundation-private/quarantine/company/document",
                    request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = new SupabaseFileStorage(
            client,
            new SupabaseStorageOptions(
                client.BaseAddress,
                "not-a-credential",
                SupabaseStorageOptions.PrivateContainerId));

        var upload = await storage.CreateSignedUploadUrlAsync(
            "quarantine/company/document", TimeSpan.FromMinutes(1), CancellationToken.None);
        var download = await storage.CreateSignedDownloadUrlAsync(
            "quarantine/company/document", TimeSpan.FromMinutes(1), CancellationToken.None);
        var objects = await storage.ListObjectsAsync("quarantine", CancellationToken.None);
        await storage.DeleteObjectAsync("quarantine/company/document", CancellationToken.None);

        Assert.Equal("https://storage.example.test/signed-upload", upload.Url.AbsoluteUri);
        Assert.Equal("https://storage.example.test/signed-download", download.Url.AbsoluteUri);
        Assert.Equal("quarantine/company/document", Assert.Single(objects).Key);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class SequencedResponseHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responseIndex >= responses.Length)
            {
                throw new InvalidOperationException("No test response is available.");
            }

            return Task.FromResult(responses[_responseIndex++]);
        }
    }

    private sealed class RequestSequenceHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int responseIndex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (responseIndex >= responses.Length)
            {
                throw new InvalidOperationException("No request response is available.");
            }

            return Task.FromResult(responses[responseIndex++](request));
        }
    }
}
