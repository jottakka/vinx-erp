using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Platform;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests.PlatformOperator;

public sealed class SupabaseAdminMfaAssuranceVerifierTests
{
    private const string Issuer = "https://project.supabase.co/auth/v1";
    private const string Audience = "authenticated";
    private const string ServiceRoleKey = "test-service-role-key";
    private const string Subject = "external-subject";

    [Fact]
    public async Task Verify_async_reports_a_current_verified_factor_and_targets_the_admin_user_endpoint()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, AdminUserJson(Subject, VerifiedFactorJson)));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorPresent, result.State);
        Assert.Equal(
            $"{Issuer}/admin/users/{Subject}",
            handler.RequestUri
        );
        Assert.Equal($"Bearer {ServiceRoleKey}", handler.Authorization);
        Assert.Equal(ServiceRoleKey, handler.ApiKey);
    }

    [Fact]
    public async Task Verify_async_counts_an_unverified_factor_as_absent()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, AdminUserJson(Subject, UnverifiedFactorJson)));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorAbsent, result.State);
    }

    [Theory]
    [InlineData("{\"id\":\"external-subject\"}")]
    [InlineData("{\"id\":\"external-subject\",\"factors\":[]}")]
    [InlineData("{\"id\":\"external-subject\",\"factors\":[{}]}")]
    [InlineData("{\"id\":\"external-subject\",\"factors\":[{\"id\":\"f1\",\"factor_type\":\"totp\",\"status\":\"unverified\"}]}")]
    [InlineData("[]")]
    public async Task Verify_async_reports_absence_without_a_verified_factor(string body)
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorAbsent, result.State);
    }

    [Fact]
    public async Task Verify_async_treats_an_unknown_subject_as_an_absent_factor()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.NotFound, "provider-body-must-not-escape"));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorAbsent, result.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Verify_async_fails_closed_for_redirect_or_error_status(
        HttpStatusCode statusCode
    )
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(statusCode, "provider-error-body-must-not-escape"));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, result.State);
    }

    [Fact]
    public async Task Verify_async_fails_closed_when_the_provider_times_out()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new TaskCanceledException("provider timeout"));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, result.State);
    }

    [Fact]
    public async Task Verify_async_fails_closed_when_the_endpoint_is_unreachable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("connection refused"));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, result.State);
    }

    [Fact]
    public async Task Verify_async_fails_closed_for_an_oversized_provider_body()
    {
        var oversizedFactors = new string('f', 32 * 1024 + 1);
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            AdminUserJson(Subject, $"{{\"id\":\"{oversizedFactors}\",\"factor_type\":\"totp\",\"status\":\"verified\"}}")));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, result.State);
    }

    [Fact]
    public async Task Verify_async_fails_closed_for_a_response_about_another_subject()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, AdminUserJson("other-subject", VerifiedFactorJson)));
        var (verifier, _) = Verifier(handler);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, result.State);
    }

    [Fact]
    public async Task Verify_async_fails_closed_without_calling_the_provider_when_configuration_is_missing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("The provider must not be called."));
        var verifier = new SupabaseAdminMfaAssuranceVerifier(
            new StubHttpClientFactory(new HttpClient(handler)),
            new SupabaseAuthOptions { Issuer = Issuer, Audience = Audience },
            new SupabaseAdminOptions { ServiceRoleKey = string.Empty },
            new MfaAssuranceCache(),
            NullLogger<SupabaseAdminMfaAssuranceVerifier>.Instance);

        var result = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, result.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Verify_async_propagates_caller_cancellation()
    {
        var handler = new StubHttpMessageHandler(
            _ => Json(HttpStatusCode.OK, AdminUserJson(Subject, VerifiedFactorJson)),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Json(HttpStatusCode.OK, AdminUserJson(Subject, VerifiedFactorJson));
            }
        );
        var (verifier, _) = Verifier(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(Subject, cancellation.Token)
        );
    }

    [Fact]
    public async Task Verify_async_reuses_the_cached_result_within_the_cache_lifetime()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, AdminUserJson(Subject, VerifiedFactorJson)));
        var (verifier, _) = Verifier(handler, timeProvider);

        var first = await verifier.VerifyAsync(Subject, CancellationToken.None);
        var second = await verifier.VerifyAsync(Subject, CancellationToken.None);
        timeProvider.Advance(MfaAssuranceCache.Lifetime - TimeSpan.FromSeconds(1));
        var third = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorPresent, first.State);
        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorPresent, second.State);
        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorPresent, third.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Verify_async_verifies_again_after_the_cache_lifetime_expires()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, AdminUserJson(Subject, VerifiedFactorJson)));
        var (verifier, _) = Verifier(handler, timeProvider);

        await verifier.VerifyAsync(Subject, CancellationToken.None);
        timeProvider.Advance(MfaAssuranceCache.Lifetime);
        await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Verify_async_does_not_cache_an_unavailable_result()
    {
        var respondUnavailable = true;
        var handler = new StubHttpMessageHandler(_ =>
            respondUnavailable
                ? Json(HttpStatusCode.BadGateway, "provider-error")
                : Json(HttpStatusCode.OK, AdminUserJson(Subject, VerifiedFactorJson)));
        var (verifier, _) = Verifier(handler);

        var unavailable = await verifier.VerifyAsync(Subject, CancellationToken.None);
        respondUnavailable = false;
        var recovered = await verifier.VerifyAsync(Subject, CancellationToken.None);

        Assert.Equal(MfaAssuranceVerificationState.Unavailable, unavailable.State);
        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorPresent, recovered.State);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Verify_async_does_not_share_cached_results_between_subjects()
    {
        var handler = new StubHttpMessageHandler(request => Json(
            HttpStatusCode.OK,
            AdminUserJson(RequestedSubject(request), VerifiedFactorJson)));
        var (verifier, _) = Verifier(handler);

        await verifier.VerifyAsync(Subject, CancellationToken.None);
        await verifier.VerifyAsync("other-subject", CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    private static (SupabaseAdminMfaAssuranceVerifier Verifier, MfaAssuranceCache Cache) Verifier(
        StubHttpMessageHandler handler,
        TimeProvider? timeProvider = null
    )
    {
        var cache = timeProvider is null
            ? new MfaAssuranceCache()
            : new MfaAssuranceCache(timeProvider);
        return (
            new SupabaseAdminMfaAssuranceVerifier(
                new StubHttpClientFactory(new HttpClient(handler)),
                new SupabaseAuthOptions { Issuer = Issuer, Audience = Audience },
                new SupabaseAdminOptions { ServiceRoleKey = ServiceRoleKey },
                cache,
                NullLogger<SupabaseAdminMfaAssuranceVerifier>.Instance),
            cache
        );
    }

    private static string RequestedSubject(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var prefix = "/admin/users/";
        return path.StartsWith(prefix, StringComparison.Ordinal)
            ? Uri.UnescapeDataString(path[prefix.Length..])
            : string.Empty;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content) =>
        new(statusCode) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private const string VerifiedFactorJson =
        "{\"id\":\"f1\",\"friendly_name\":null,\"factor_type\":\"totp\",\"status\":\"verified\",\"created_at\":\"2026-08-15T00:00:00Z\"}";

    private const string UnverifiedFactorJson =
        "{\"id\":\"f1\",\"friendly_name\":null,\"factor_type\":\"totp\",\"status\":\"unverified\",\"created_at\":\"2026-08-15T00:00:00Z\"}";

    private static string AdminUserJson(string id, string factorJson) =>
        $$"""
            {
              "id": "{{id}}",
              "aud": "{{Audience}}",
              "email": "operator@example.test",
              "email_confirmed_at": "2026-08-15T00:00:00Z",
              "is_anonymous": false,
              "factors": [{{factorJson}}]
            }
            """;

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> sendAsync,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendAsyncWithCancellation = null
    ) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }
        public int CallCount => Volatile.Read(ref _callCount);

        private int _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _callCount);
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("apikey", out var values)
                ? Assert.Single(values)
                : null;
            if (sendAsyncWithCancellation is not null)
            {
                return await sendAsyncWithCancellation(request, cancellationToken);
            }
            return sendAsync(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _now;

        private DateTimeOffset _now = now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
