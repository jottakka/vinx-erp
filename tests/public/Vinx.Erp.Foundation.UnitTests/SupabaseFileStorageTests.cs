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
}
