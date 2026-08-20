using System.Net;
using System.Text;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Platform;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests.PlatformOperator;

public sealed class SupabaseAdminUserVerifierTests
{
    private const string Issuer = "https://project.supabase.co/auth/v1/";
    private const string Audience = "authenticated";
    private const string ServiceRoleKey = "test-service-role-key";
    private const string Subject = "operator/with reserved?characters";

    [Fact]
    public async Task Verify_async_returns_verified_only_for_an_exact_confirmed_non_anonymous_user()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, UserJson(Subject)));
        var verifier = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.Verified, result.Category);
        Assert.True(result.TryGetVerifiedEmail(out var email));
        Assert.Equal("operator@example.test", email);
        Assert.DoesNotContain(email, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            $"https://project.supabase.co/auth/v1/admin/users/{Uri.EscapeDataString(Subject)}",
            handler.RequestUri
        );
        Assert.Equal($"Bearer {ServiceRoleKey}", handler.Authorization);
        Assert.Equal(ServiceRoleKey, handler.ApiKey);
    }

    [Fact]
    public async Task Verify_async_returns_not_found_without_exposing_the_provider_body()
    {
        const string body = "provider-body-must-not-escape";
        var verifier = Verifier(
            new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound, body))
        );

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.NotFound, result.Category);
        Assert.False(result.TryGetVerifiedEmail(out _));
        Assert.DoesNotContain(body, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData(
        "{\"id\":\"operator/with reserved?characters\",\"aud\":\"other-audience\",\"is_anonymous\":false,\"email\":\"operator@example.test\",\"email_confirmed_at\":\"2026-08-15T00:00:00Z\",\"confirmed_at\":\"2026-08-15T00:00:00Z\",\"identities\":[]}"
    )]
    [InlineData(
        "{\"id\":\"operator/with reserved?characters\",\"aud\":\"authenticated\",\"is_anonymous\":true,\"email\":\"operator@example.test\",\"email_confirmed_at\":\"2026-08-15T00:00:00Z\",\"confirmed_at\":\"2026-08-15T00:00:00Z\"}"
    )]
    [InlineData(
        "{\"id\":\"operator/with reserved?characters\",\"aud\":\"authenticated\",\"is_anonymous\":false,\"email\":\"operator@example.test\",\"email_confirmed_at\":\"2026-08-15T00:00:00Z\",\"confirmed_at\":\"2026-08-15T00:00:00Z\",\"identities\":[]}"
    )]
    [InlineData(
        "{\"id\":\"operator/with reserved?characters\",\"aud\":\"authenticated\",\"is_anonymous\":false,\"email\":\"\",\"email_confirmed_at\":\"2026-08-15T00:00:00Z\",\"confirmed_at\":\"2026-08-15T00:00:00Z\",\"banned_until\":\"2026-08-16T00:00:00Z\"}"
    )]
    [InlineData(
        "{\"id\":\"operator/with reserved?characters\",\"aud\":\"authenticated\",\"is_anonymous\":false,\"email\":\"operator@example.test\",\"email_confirmed_at\":\"2026-08-15T00:00:00Z\",\"confirmed_at\":\"2026-08-15T00:00:00Z\",\"deleted_at\":\"2026-08-16T00:00:00Z\"}"
    )]
    public async Task Verify_async_rejects_malformed_or_invalid_provider_state(string body)
    {
        var verifier = Verifier(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)));

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.InvalidUser, result.Category);
        Assert.DoesNotContain("operator@example.test", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_async_accepts_a_confirmed_email_when_only_one_confirmation_timestamp_is_present()
    {
        var body = UserJson(Subject)
            .Replace(
                "\"confirmed_at\": \"2026-08-15T00:00:00Z\",",
                "\"confirmed_at\": null,",
                StringComparison.Ordinal
            );
        var verifier = Verifier(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)));

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.Verified, result.Category);
    }

    [Fact]
    public async Task Verify_async_rejects_a_phone_only_confirmation_when_email_is_not_confirmed()
    {
        var body = UserJson(Subject)
            .Replace(
                "\"email_confirmed_at\": \"2026-08-15T00:00:00Z\",",
                "\"email_confirmed_at\": null, \"phone_confirmed_at\": \"2026-08-15T00:00:00Z\",",
                StringComparison.Ordinal
            );
        var verifier = Verifier(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)));

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.InvalidUser, result.Category);
        Assert.False(result.TryGetVerifiedEmail(out _));
    }

    [Fact]
    public async Task Verify_async_rejects_a_provider_user_with_a_different_id()
    {
        var verifier = Verifier(
            new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, UserJson("other-subject")))
        );

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.SubjectMismatch, result.Category);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Verify_async_returns_provider_unavailable_for_redirect_or_non_success_status(
        HttpStatusCode statusCode
    )
    {
        const string body = "provider-error-body-must-not-escape";
        var verifier = Verifier(new StubHttpMessageHandler(_ => Json(statusCode, body)));

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.ProviderUnavailable, result.Category);
        Assert.DoesNotContain(body, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_async_returns_provider_unavailable_when_the_provider_times_out()
    {
        var verifier = Verifier(
            new StubHttpMessageHandler(_ => throw new TaskCanceledException("provider timeout"))
        );

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(PlatformOperatorVerificationCategory.ProviderUnavailable, result.Category);
    }

    [Fact]
    public async Task Verify_async_propagates_caller_cancellation()
    {
        var verifier = Verifier(
            new StubHttpMessageHandler(
                async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Json(HttpStatusCode.OK, UserJson(Subject));
                }
            )
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(Subject, cancellation.Token)
        );
    }

    private static SupabaseAdminUserVerifier Verifier(StubHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new SupabaseAuthOptions { Issuer = Issuer, Audience = Audience },
            new SupabaseAdminOptions { ServiceRoleKey = ServiceRoleKey }
        );

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content) =>
        new(statusCode) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static string UserJson(string id) =>
        $$"""
            {
              "id": "{{id}}",
              "aud": "authenticated",
              "role": "authenticated",
              "email": "operator@example.test",
              "email_confirmed_at": "2026-08-15T00:00:00Z",
              "confirmed_at": "2026-08-15T00:00:00Z",
              "phone": "",
              "last_sign_in_at": "2026-08-15T00:00:00Z",
              "app_metadata": { "provider": "email" },
              "user_metadata": { "role": "platform-operator" },
              "identities": [
                {
                  "identity_id": "identity-1",
                  "id": "{{id}}",
                  "user_id": "{{id}}",
                  "identity_data": { "email": "operator@example.test" },
                  "provider": "email",
                  "last_sign_in_at": "2026-08-15T00:00:00Z",
                  "created_at": "2026-08-15T00:00:00Z",
                  "updated_at": "2026-08-15T00:00:00Z"
                }
              ],
              "created_at": "2026-08-15T00:00:00Z",
              "updated_at": "2026-08-15T00:00:00Z",
              "is_anonymous": false
            }
            """;

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync
    ) : HttpMessageHandler
    {
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request))) { }

        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("apikey", out var values)
                ? Assert.Single(values)
                : null;
            return sendAsync(request, cancellationToken);
        }
    }
}
