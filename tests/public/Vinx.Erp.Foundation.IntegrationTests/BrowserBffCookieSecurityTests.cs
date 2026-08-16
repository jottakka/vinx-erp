using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BrowserBffCookieSecurityTests
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private const string Email = "same-address@example.test";
    private const string Password = "safe-test-password";

    [Fact]
    public async Task Sign_in_sets_private_no_store_and_a_nonpersistent_proof_only_cookie()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        var accessToken = factory.CreateJwtToken(JwtTokenVariation.Valid);
        const string refreshToken = "refresh-token-must-not-reach-browser";
        provider.ResponseFactory = _ => ProviderResponse(accessToken, refreshToken);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var response = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var cookie = GetBrowserCookie(response);
        Assert.DoesNotContain(accessToken, cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; max-age=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_out_clears_the_browser_cookie_with_private_no_store()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            factory.CreateJwtToken(JwtTokenVariation.Valid), null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await SignInAsync(client);
        signIn.EnsureSuccessStatusCode();

        using var response = await SendSignOutAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var clearedCookie = GetBrowserCookie(response);
        Assert.Contains("expires=", clearedCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_authenticated_mutation_requires_csrf_while_bearer_mutation_does_not()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            factory.CreateJwtToken(JwtTokenVariation.Valid), null);

        using var cookieClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await SignInAsync(cookieClient);
        signIn.EnsureSuccessStatusCode();

        using var bearerClient = factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var bearerResponse = await bearerClient.PostAsync(
            "/foundation/identity/sessions/revoke-all", content: null);
        Assert.Equal(HttpStatusCode.NoContent, bearerResponse.StatusCode);

        using var cookieResponse = await cookieClient.PostAsync(
            "/foundation/identity/sessions/revoke-all", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, cookieResponse.StatusCode);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email = Email, password = Password }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendSignOutAsync(HttpClient client)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, "/foundation/identity/browser-session");
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The test antiforgery token was empty.");
    }

    private static string GetBrowserCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Single(value => value.StartsWith("vinx.session=", StringComparison.Ordinal))
            : throw new Xunit.Sdk.XunitException("The response did not set the browser session cookie.");

    private static HttpResponseMessage ProviderResponse(string accessToken, string? refreshToken) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { access_token = accessToken, refresh_token = refreshToken }),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(ResponseFactory?.Invoke(request)
                ?? throw new InvalidOperationException("The provider response was not configured."));
    }
}
