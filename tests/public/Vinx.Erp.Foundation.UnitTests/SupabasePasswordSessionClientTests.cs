using System.Net;
using System.Text;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class SupabasePasswordSessionClientTests
{
    private const string PublishableKey = "publishable-key-for-tests";
    private const string Email = "person@example.test";
    private const string Password = "correct-horse-battery-staple";
    private const string AccessToken = "test-access-token";
    private const string RefreshToken = "test-refresh-token";

    [Fact]
    public async Task Sign_in_posts_exact_password_grant_request_without_authorization_header()
    {
        using var handler = new FakeSupabaseHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://project.supabase.co/auth/v1/token?grant_type=password", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("{\"email\":\"person@example.test\",\"password\":\"correct-horse-battery-staple\"}",
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(PublishableKey, request.Headers.GetValues("apikey").Single());
            Assert.Null(request.Headers.Authorization);

            return Task.FromResult(JsonResponse("{\"access_token\":\"test-access-token\",\"refresh_token\":\"test-refresh-token\"}"));
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://project.supabase.co/auth/v1/")
        };
        var client = CreateClient(httpClient);

        var result = await client.SignInAsync(Email, Password, CancellationToken.None);

        Assert.Equal(SupabasePasswordSessionState.Succeeded, result.State);
    }

    [Fact]
    public async Task Successful_sign_in_returns_only_access_token_and_never_exposes_tokens_in_string_form()
    {
        using var handler = new FakeSupabaseHandler((_, _) => Task.FromResult(
            JsonResponse("{\"access_token\":\"test-access-token\",\"refresh_token\":\"test-refresh-token\"}")));
        using var httpClient = CreateHttpClient(handler);
        var result = await CreateClient(httpClient).SignInAsync(Email, Password, CancellationToken.None);

        Assert.Equal(SupabasePasswordSessionState.Succeeded, result.State);
        Assert.True(result.TryGetAccessToken(out var accessToken));
        Assert.Equal(AccessToken, accessToken);
        Assert.DoesNotContain(RefreshToken, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Bad_credentials_statuses_return_invalid_credentials_without_a_token(HttpStatusCode statusCode)
    {
        const string providerBody = "provider-body-invalid-credentials";
        var result = await SignInWithResponseAsync(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(providerBody, Encoding.UTF8, "application/json")
        });

        AssertNoToken(result, SupabasePasswordSessionState.InvalidCredentials, providerBody);
    }

    [Fact]
    public async Task Rate_limited_status_returns_rate_limited_without_a_token()
    {
        const string providerBody = "provider-body-rate-limit";
        var result = await SignInWithResponseAsync(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(providerBody, Encoding.UTF8, "application/json")
        });

        AssertNoToken(result, SupabasePasswordSessionState.RateLimited, providerBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "provider-body-redirect")]
    [InlineData(HttpStatusCode.InternalServerError, "provider-body-server-error")]
    public async Task Redirect_and_server_error_statuses_return_unavailable_without_provider_details(
        HttpStatusCode statusCode,
        string providerBody)
    {
        var result = await SignInWithResponseAsync(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(providerBody, Encoding.UTF8, "text/plain")
        });

        AssertNoToken(result, SupabasePasswordSessionState.Unavailable, providerBody);
    }

    [Theory]
    [InlineData("not-json", "not-json")]
    [InlineData("{\"error\":\"provider-detail\"}", "provider-detail")]
    [InlineData("{\"refresh_token\":\"provider-refresh-only\"}", "provider-refresh-only")]
    public async Task Malformed_or_tokenless_success_bodies_return_unavailable_without_provider_details(
        string providerBody,
        string forbiddenDetail)
    {
        var result = await SignInWithResponseAsync(JsonResponse(providerBody));

        AssertNoToken(result, SupabasePasswordSessionState.Unavailable, forbiddenDetail);
    }

    [Fact]
    public async Task Oversized_provider_body_returns_unavailable_without_body_or_token()
    {
        var providerBody = new string('x', 2 * 1024 * 1024);
        var result = await SignInWithResponseAsync(JsonResponse(providerBody));

        AssertNoToken(result, SupabasePasswordSessionState.Unavailable, providerBody);
    }

    [Fact]
    public async Task Http_timeout_returns_unavailable_without_provider_details()
    {
        using var handler = new FakeSupabaseHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The fake handler should be cancelled by the client timeout.");
        });
        using var httpClient = CreateHttpClient(handler);
        httpClient.Timeout = TimeSpan.FromMilliseconds(20);

        var result = await CreateClient(httpClient).SignInAsync(Email, Password, CancellationToken.None);

        AssertNoToken(result, SupabasePasswordSessionState.Unavailable, "The fake handler");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_unavailable()
    {
        using var handler = new FakeSupabaseHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The fake handler should be cancelled by the caller.");
        });
        using var httpClient = CreateHttpClient(handler);
        using var cancellation = new CancellationTokenSource();

        var signIn = CreateClient(httpClient).SignInAsync(Email, Password, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
    }

    private static async Task<SupabasePasswordSessionResult> SignInWithResponseAsync(HttpResponseMessage response)
    {
        using var handler = new FakeSupabaseHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        return await CreateClient(httpClient).SignInAsync(Email, Password, CancellationToken.None);
    }

    private static SupabasePasswordSessionClient CreateClient(HttpClient httpClient) =>
        new(httpClient, new SupabaseBrowserSessionOptions { PublishableKey = PublishableKey });

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://project.supabase.co/auth/v1/")
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static void AssertNoToken(
        SupabasePasswordSessionResult result,
        SupabasePasswordSessionState expectedState,
        string forbiddenDetail)
    {
        Assert.Equal(expectedState, result.State);
        Assert.False(result.TryGetAccessToken(out var accessToken));
        Assert.Null(accessToken);
        Assert.DoesNotContain(AccessToken, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenDetail, result.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeSupabaseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
