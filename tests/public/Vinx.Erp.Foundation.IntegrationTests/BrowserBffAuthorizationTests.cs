using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BrowserBffAuthorizationTests
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";

    [Fact]
    public async Task Bearer_and_browser_cookie_on_cookie_only_sign_out_returns_empty_403_without_clear_or_revocation()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var signIn = await SignInAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid));
        using var response = await client.DeleteAsync("/foundation/identity/browser-session");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            GetSetCookies(response),
            cookie => cookie.StartsWith("vinx.session=", StringComparison.Ordinal));

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var sessions = await administrator.UserSessions.ToListAsync();
        Assert.NotEmpty(sessions);
        Assert.All(sessions, session => Assert.Null(session.RevokedAt));
    }

    [Fact]
    public async Task Anonymous_browser_session_delete_returns_empty_401_without_revocation_side_effect()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        using var response = await factory.CreateClient().SendAsync(
            new HttpRequestMessage(
                HttpMethod.Delete,
                "/foundation/identity/browser-session"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        using var tokenResponse = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery");
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var antiforgeryToken = tokenDocument.RootElement.GetProperty("requestToken").GetString()
            ?? throw new Xunit.Sdk.XunitException("The antiforgery token was empty.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    email = "same-address@example.test",
                    password = "safe-test-password",
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(AntiforgeryHeader, antiforgeryToken);
        return await client.SendAsync(request);
    }

    private static IEnumerable<string> GetSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static HttpResponseMessage ProviderResponse(string accessToken) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { access_token = accessToken }),
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
                ?? throw new InvalidOperationException("The browser session provider response was not configured."));
    }
}
