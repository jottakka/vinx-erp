using System.Net;
using System.Text.Json;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BrowserBffEndpointTests
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";

    [Fact]
    public async Task Browser_antiforgery_returns_a_private_no_store_request_token_to_an_anonymous_browser()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        using var response = await factory.CreateClient().GetAsync(
            "/foundation/identity/browser-session/antiforgery");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["requestToken"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var requestToken = document.RootElement.GetProperty("requestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestToken));
    }

    [Fact]
    public async Task Browser_antiforgery_is_not_rate_limited_by_the_login_limit()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var client = factory.CreateClient();

        for (var requestNumber = 1; requestNumber <= 101; requestNumber++)
        {
            using var response = await client.GetAsync(
                "/foundation/identity/browser-session/antiforgery");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/foundation/identity/browser-session/")]
    [InlineData("/FOUNDATION/IDENTITY/BROWSER-SESSION")]
    public async Task Browser_session_alias_shares_the_global_login_limit(string alias)
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);

        for (var attempt = 1; attempt <= 100; attempt++)
        {
            using var response = await SendMalformedSignInAsync(
                client,
                "/foundation/identity/browser-session",
                token,
                $"10.0.0.{attempt}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var limited = await SendMalformedSignInAsync(client, alias, token, "10.0.0.101");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Browser_session_without_antiforgery_is_bad_request_once_the_route_exists()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        using var response = await factory.CreateClient().PostAsync(
            "/foundation/identity/browser-session",
            new StringContent("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Browser_session_with_valid_antiforgery_rejects_malformed_json_safely()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var client = factory.CreateClient();

        using var tokenResponse = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery");
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(
            await tokenResponse.Content.ReadAsStringAsync());
        var requestToken = tokenDocument.RootElement.GetProperty("requestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestToken));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                "{",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(AntiforgeryHeader, requestToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browser_session_rejects_unknown_json_fields_without_calling_identity_provider()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid));

        using var tokenResponse = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery");
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(
            await tokenResponse.Content.ReadAsStringAsync());
        var requestToken = tokenDocument.RootElement.GetProperty("requestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestToken));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                "{\"email\":\"same-address@example.test\",\"password\":\"password\",\"unknown\":true}",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(AntiforgeryHeader, requestToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.IdentityProviderCallCount);
    }

    [Fact]
    public async Task Browser_session_rejects_duplicate_email_keys_without_calling_identity_provider()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid));

        using var tokenResponse = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery");
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(
            await tokenResponse.Content.ReadAsStringAsync());
        var requestToken = tokenDocument.RootElement.GetProperty("requestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestToken));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                "{\"email\":\"first@example.test\",\"email\":\"second@example.test\",\"password\":\"password\"}",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(AntiforgeryHeader, requestToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.IdentityProviderCallCount);
    }

    [Fact]
    public async Task Anonymous_browser_bootstrap_remains_unauthorized()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The antiforgery request token was empty.");
    }

    private static async Task<HttpResponseMessage> SendMalformedSignInAsync(
        HttpClient client,
        string path,
        string antiforgeryToken,
        string remoteIpAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{"),
        };
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);
        request.Headers.Add("X-Test-Remote-IP", remoteIpAddress);
        return await client.SendAsync(request);
    }
}
