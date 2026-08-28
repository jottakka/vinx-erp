using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BrowserBffSessionClaimBoundaryTests
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private const string BrowserCookieScheme = "BrowserSessionCookie";
    private const string Email = "same-address@example.test";
    private const string RefreshToken = "refresh-token-must-not-reach-browser";

    [Fact]
    public async Task Password_sign_in_cookie_ticket_contains_only_bounded_proof_claims_and_access_token_expiry()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        var accessToken = factory.CreateJwtToken(JwtTokenVariation.AmrWithProviderOnlyMetadata);
        provider.ResponseFactory = _ => ProviderResponse(accessToken, RefreshToken);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var signIn = await SignInAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);

        var ticket = UnprotectBrowserCookie(factory, GetBrowserCookie(signIn));
        var signedToken = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var allowedTypes = new[] { "amr", "exp", "iat", "iss", "session_id", "sub" };

        Assert.Equal(
            allowedTypes,
            ticket.Principal.Claims
                .Select(claim => claim.Type)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(type => type, StringComparer.Ordinal));
        Assert.Equal(
            signedToken.Claims.Single(claim => claim.Type == "iss").Value,
            ticket.Principal.Claims.Single(claim => claim.Type == "iss").Value);
        Assert.Equal(
            signedToken.Claims.Single(claim => claim.Type == "sub").Value,
            ticket.Principal.Claims.Single(claim => claim.Type == "sub").Value);
        Assert.Equal(
            signedToken.Claims.Single(claim => claim.Type == "session_id").Value,
            ticket.Principal.Claims.Single(claim => claim.Type == "session_id").Value);
        Assert.Equal(
            signedToken.Claims.Single(claim => claim.Type == "iat").Value,
            ticket.Principal.Claims.Single(claim => claim.Type == "iat").Value);
        Assert.Equal(
            signedToken.Claims.Single(claim => claim.Type == "exp").Value,
            ticket.Principal.Claims.Single(claim => claim.Type == "exp").Value);

        var amrEntries = ticket.Principal.Claims
            .Where(claim => claim.Type == "amr")
            .ToArray();
        Assert.Equal(2, amrEntries.Length);
        Assert.Contains(amrEntries, claim => claim.Value.Contains("\"method\":\"password\"", StringComparison.Ordinal));
        Assert.Contains(amrEntries, claim => claim.Value.Contains("\"method\":\"email\"", StringComparison.Ordinal));
        Assert.All(amrEntries, claim =>
        {
            using var document = JsonDocument.Parse(claim.Value);
            Assert.Equal(
                new[] { "method", "timestamp" },
                document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.DoesNotContain("provider_session_metadata", claim.Value, StringComparison.Ordinal);
        });

        Assert.DoesNotContain(ticket.Principal.Claims, claim =>
            claim.Value.Contains(Email, StringComparison.OrdinalIgnoreCase)
            || claim.Value.Contains(accessToken, StringComparison.Ordinal)
            || claim.Value.Contains(RefreshToken, StringComparison.Ordinal));
        Assert.DoesNotContain(
            ticket.Properties.Items.Keys,
            key => key.StartsWith(".Token.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ticket.Properties.Items.Values,
            value => value is not null
                && (value.Contains(accessToken, StringComparison.Ordinal)
                    || value.Contains(RefreshToken, StringComparison.Ordinal)));
        Assert.False(ticket.Properties.IsPersistent);
        Assert.Equal(false, ticket.Properties.AllowRefresh);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(signedToken.Payload.Expiration!.Value),
            ticket.Properties.ExpiresUtc);
    }

    [Fact]
    public async Task Browser_cookie_keeps_only_canonical_amr_proof_fields_after_password_sign_in()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            factory.CreateJwtToken(JwtTokenVariation.AmrWithProviderOnlyMetadata));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var signIn = await SignInAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);

        using var response = await client.GetAsync("/test/browser-bff/claims");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var amrEntries = document.RootElement.GetProperty("amr").EnumerateArray().ToArray();
        Assert.NotEmpty(amrEntries);
        Assert.All(amrEntries, entry =>
        {
            using var amr = JsonDocument.Parse(entry.GetString()
                ?? throw new Xunit.Sdk.XunitException("The authenticated AMR claim was empty."));
            Assert.True(amr.RootElement.TryGetProperty("method", out var method));
            Assert.Equal(JsonValueKind.String, method.ValueKind);
            Assert.True(amr.RootElement.TryGetProperty("timestamp", out var timestamp));
            Assert.Equal(JsonValueKind.Number, timestamp.ValueKind);
            Assert.Equal(2, amr.RootElement.EnumerateObject().Count());
            Assert.False(amr.RootElement.TryGetProperty("provider_session_metadata", out _));
            Assert.DoesNotContain("not-for-browser", entry.GetString(), StringComparison.Ordinal);
        });
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email = "same-address@example.test", password = "safe-test-password" }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);
        return await client.SendAsync(request);
    }

    private static string GetBrowserCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Single(value => value.StartsWith("vinx.session=", StringComparison.Ordinal))
                .Split(';', 2)[0]["vinx.session=".Length..]
            : throw new Xunit.Sdk.XunitException("The response did not set the browser session cookie.");

    private static Microsoft.AspNetCore.Authentication.AuthenticationTicket UnprotectBrowserCookie(
        FoundationApiFactory factory,
        string cookie)
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(BrowserCookieScheme);
        return options.TicketDataFormat!.Unprotect(cookie)
            ?? throw new Xunit.Sdk.XunitException("The browser session cookie could not be unprotected.");
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/foundation/identity/browser-session/antiforgery");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new Xunit.Sdk.XunitException("The antiforgery token was empty.");
    }

    private static HttpResponseMessage ProviderResponse(string accessToken, string? refreshToken = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { access_token = accessToken, refresh_token = refreshToken }),
            Encoding.UTF8,
            "application/json"),
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
