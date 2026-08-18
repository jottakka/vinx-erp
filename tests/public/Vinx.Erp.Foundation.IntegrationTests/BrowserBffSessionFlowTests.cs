using System.Security.Claims;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BrowserBffSessionFlowTests
{
    private const string Email = "same-address@example.test";
    private const string Password = "safe-test-password";
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";

    [Fact]
    public async Task Password_grant_creates_a_proof_only_browser_cookie_that_authenticates_bootstrap()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        var accessToken = factory.CreateJwtToken(JwtTokenVariation.Valid);
        const string refreshToken = "provider-refresh-token-must-not-reach-browser";
        provider.ResponseFactory = _ => ProviderResponse(HttpStatusCode.OK, accessToken, refreshToken);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var signIn = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var browserCookie = GetBrowserSessionCookie(signIn);
        Assert.DoesNotContain(accessToken, browserCookie, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, browserCookie, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, browserCookie, StringComparison.OrdinalIgnoreCase);

        using var bootstrap = await client.GetAsync("/foundation/bootstrap");

        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
    }

    [Fact]
    public async Task Malformed_authorization_header_never_falls_back_to_a_valid_browser_cookie()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            HttpStatusCode.OK,
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await SignInAsync(client);
        signIn.EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/foundation/bootstrap");
        request.Headers.TryAddWithoutValidation("Authorization", "malformed");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_provider_credentials_return_empty_401_without_a_browser_session_cookie()
    {
        var provider = new BrowserSessionProviderHandler
        {
            ResponseFactory = _ => ProviderResponse(HttpStatusCode.Unauthorized),
        };
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var response = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(GetSetCookies(response), value => IsBrowserSessionCookie(value));
    }

    [Fact]
    public async Task Oversized_login_body_is_rejected_without_calling_provider_or_setting_a_browser_session_cookie()
    {
        var providerCalls = 0;
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
        {
            providerCalls++;
            return ProviderResponse(
                HttpStatusCode.OK,
                factory.CreateJwtToken(JwtTokenVariation.Valid));
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        var oversizedBody = new string(' ', 8193)
            + JsonSerializer.Serialize(new { email = Email, password = Password });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/foundation/identity/browser-session")
        {
            Content = new StringContent(oversizedBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, providerCalls);
        Assert.DoesNotContain(GetSetCookies(response), IsBrowserSessionCookie);
    }

    [Fact]
    public async Task Chunked_oversized_login_body_is_rejected_without_calling_provider_or_setting_a_browser_session_cookie()
    {
        var providerCalls = 0;
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
        {
            providerCalls++;
            return ProviderResponse(
                HttpStatusCode.OK,
                factory.CreateJwtToken(JwtTokenVariation.Valid));
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        var oversizedBody = new string(' ', 8193)
            + JsonSerializer.Serialize(new { email = Email, password = Password });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/foundation/identity/browser-session")
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversizedBody))),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, providerCalls);
        Assert.DoesNotContain(GetSetCookies(response), IsBrowserSessionCookie);
    }

    [Fact]
    public async Task Provider_failure_returns_safe_503_without_a_browser_session_cookie()
    {
        var provider = new BrowserSessionProviderHandler
        {
            ResponseFactory = _ => ProviderResponse(HttpStatusCode.BadGateway),
        };
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var response = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"code\":\"identity_provider_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(GetSetCookies(response), value => IsBrowserSessionCookie(value));
    }

    [Fact]
    public async Task Identity_resolution_failure_returns_safe_503_without_a_browser_session_cookie()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(
                provider,
                databaseFailurePoint: GateDatabaseFailurePoint.Resolution);
        provider.ResponseFactory = _ => ProviderResponse(
            HttpStatusCode.OK,
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var response = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"code\":\"identity_provider_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(GetSetCookies(response), value => IsBrowserSessionCookie(value));
    }

    [Fact]
    public async Task Unprovisioned_provider_identity_returns_empty_401_without_a_browser_session_cookie()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider, seedAuthenticatedActor: false);
        provider.ResponseFactory = _ => ProviderResponse(
            HttpStatusCode.OK,
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var response = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(GetSetCookies(response), value => IsBrowserSessionCookie(value));
    }

    [Fact]
    public async Task Cookie_only_sign_out_requires_antiforgery_then_revokes_all_sessions_and_clears_the_cookie()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            HttpStatusCode.OK,
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        using var signIn = await SignInAsync(client, antiforgeryToken);
        signIn.EnsureSuccessStatusCode();
        var signOutAntiforgeryToken = await GetAntiforgeryTokenAsync(client);

        using var missingAntiforgery = await client.DeleteAsync("/foundation/identity/browser-session");

        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/foundation/identity/browser-session");
        request.Headers.Add(AntiforgeryHeader, signOutAntiforgeryToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var clearedCookie = GetSetCookies(response).Single(IsBrowserSessionCookie);
        Assert.Contains("expires=", clearedCookie, StringComparison.OrdinalIgnoreCase);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.All(await administrator.UserSessions.ToListAsync(), session => Assert.NotNull(session.RevokedAt));
    }

    [Fact]
    public async Task Missing_antiforgery_on_sign_out_keeps_the_browser_session_usable()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            HttpStatusCode.OK,
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var signIn = await SignInAsync(client);
        signIn.EnsureSuccessStatusCode();

        using var missingAntiforgery = await client.DeleteAsync("/foundation/identity/browser-session");

        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        Assert.DoesNotContain(GetSetCookies(missingAntiforgery), IsBrowserSessionCookie);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.All(await administrator.UserSessions.ToListAsync(), session => Assert.Null(session.RevokedAt));

        using var bootstrap = await client.GetAsync("/foundation/bootstrap");

        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
    }

    [Fact]
    public async Task Sign_out_returns_safe_503_and_clears_the_cookie_when_global_revocation_is_unavailable()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(
                provider,
                databaseFailurePoint: GateDatabaseFailurePoint.SelfSessionRevocation);
        provider.ResponseFactory = _ => ProviderResponse(
            HttpStatusCode.OK,
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await SignInAsync(client);
        signIn.EnsureSuccessStatusCode();
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/foundation/identity/browser-session");
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"code\":\"identity_gate_unavailable\"}", await response.Content.ReadAsStringAsync());
        var clearedCookie = GetSetCookies(response).Single(IsBrowserSessionCookie);
        Assert.Contains("expires=", clearedCookie, StringComparison.OrdinalIgnoreCase);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.All(await administrator.UserSessions.ToListAsync(), session => Assert.Null(session.RevokedAt));
    }

    [Fact]
    public async Task Sign_out_returns_safe_503_and_clears_a_valid_cookie_when_session_resolution_is_unavailable()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndDatabaseFailureAsync(
            GateDatabaseFailurePoint.Resolution);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var cookie = CreateValidBrowserSessionCookie(factory);
        client.DefaultRequestHeaders.Add("Cookie", $"vinx.session={cookie}");
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/foundation/identity/browser-session");
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"code\":\"identity_gate_unavailable\"}", await response.Content.ReadAsStringAsync());
        var clearedCookie = GetSetCookies(response).Single(IsBrowserSessionCookie);
        Assert.Contains("expires=", clearedCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateValidBrowserSessionCookie(FoundationApiFactory factory)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new ClaimsIdentity(
            [
                new Claim("iss", "https://project.supabase.co/auth/v1"),
                new Claim("sub", "external-subject"),
                new Claim("session_id", Guid.NewGuid().ToString("D")),
                new Claim("iat", now.AddMinutes(-1).ToUnixTimeSeconds().ToString()),
                new Claim("exp", now.AddHours(1).ToUnixTimeSeconds().ToString()),
                new Claim("amr", "{\"method\":\"password\",\"timestamp\":0}", "JSON"),
            ],
            "BrowserSessionCookie");
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            "BrowserSessionCookie");
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("BrowserSessionCookie");
        return options.TicketDataFormat!.Protect(ticket);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string? antiforgeryToken = null)
    {
        var token = antiforgeryToken ?? await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email = Email, password = Password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/foundation/identity/browser-session/antiforgery");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var requestToken = payload.RootElement.GetProperty("requestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestToken));
        return requestToken;
    }

    private static HttpResponseMessage ProviderResponse(
        HttpStatusCode statusCode,
        string? accessToken = null,
        string? refreshToken = null)
    {
        var content = accessToken is null
            ? "{}"
            : JsonSerializer.Serialize(new { access_token = accessToken, refresh_token = refreshToken });
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }

    private static string GetBrowserSessionCookie(HttpResponseMessage response) =>
        GetSetCookies(response).Single(IsBrowserSessionCookie);

    private static IEnumerable<string> GetSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static bool IsBrowserSessionCookie(string value) =>
        value.StartsWith("vinx.session=", StringComparison.Ordinal);

    private sealed class BrowserSessionProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(ResponseFactory?.Invoke(request)
                ?? throw new InvalidOperationException("The browser session provider response was not configured."));
    }
}
