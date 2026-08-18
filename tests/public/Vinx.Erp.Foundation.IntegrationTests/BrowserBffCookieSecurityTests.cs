using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
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
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        var accessToken = factory.CreateJwtToken(JwtTokenVariation.Valid);
        const string refreshToken = "refresh-token-must-not-reach-browser";
        provider.ResponseFactory = _ => ProviderResponse(accessToken, refreshToken);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );

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
    public async Task Cookie_authenticated_responses_are_private_no_store_on_every_route()
    {
        var provider = new ProviderHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(
            factory.CreateJwtToken(JwtTokenVariation.Valid), null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await SignInAsync(client);
        signIn.EnsureSuccessStatusCode();

        using var response = await client.GetAsync("/foundation/capabilities");

        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Sign_out_clears_the_browser_cookie_with_private_no_store()
    {
        var provider = new ProviderHandler();
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
            ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid), null);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
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
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
            ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid), null);

        using var cookieClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
        using var signIn = await SignInAsync(cookieClient);
        signIn.EnsureSuccessStatusCode();

        using var bearerClient = factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid)
        );
        using var bearerResponse = await bearerClient.PostAsync(
            "/foundation/identity/sessions/revoke-all",
            content: null
        );
        Assert.Equal(HttpStatusCode.NoContent, bearerResponse.StatusCode);

        using var cookieResponse = await cookieClient.PostAsync(
            "/foundation/identity/sessions/revoke-all",
            content: null
        );
        Assert.Equal(HttpStatusCode.BadRequest, cookieResponse.StatusCode);
    }

    [Fact]
    public async Task Employee_directory_cookie_requires_csrf_then_allows_fresh_token_and_bearer()
    {
        var provider = new ProviderHandler();
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
            ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid), null);
        var companyId = await SeedAuthorizedCompanyAsync(factory);
        var route = $"/foundation/companies/{companyId}/employees";
        using var cookieClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
        using var signIn = await SignInAsync(cookieClient);
        signIn.EnsureSuccessStatusCode();

        using var missingToken = await cookieClient.PostAsJsonAsync(
            route,
            new
            {
                requestId = Guid.NewGuid(),
                companyId,
                displayName = "Cookie missing",
                email = "cookie-missing@example.test",
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.True(missingToken.Headers.CacheControl?.Private);
        Assert.True(missingToken.Headers.CacheControl?.NoStore);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
            Assert.Empty(
                await administrator
                    .Employees.Where(item => item.CompanyId == companyId)
                    .ToListAsync()
            );

        var token = await GetAntiforgeryTokenAsync(cookieClient);
        using var withToken = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(
                new
                {
                    requestId = Guid.NewGuid(),
                    companyId,
                    displayName = "Cookie fresh",
                    email = "cookie-fresh@example.test",
                }
            ),
        };
        withToken.Headers.Add(AntiforgeryHeader, token);
        using var cookieSuccess = await cookieClient.SendAsync(withToken);
        Assert.Equal(HttpStatusCode.Created, cookieSuccess.StatusCode);

        using var bearerClient = factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid)
        );
        using var bearerSuccess = await bearerClient.PostAsJsonAsync(
            route,
            new
            {
                requestId = Guid.NewGuid(),
                companyId,
                displayName = "Bearer",
                email = "bearer@example.test",
            }
        );
        Assert.Equal(HttpStatusCode.Created, bearerSuccess.StatusCode);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session"
        )
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email = Email, password = Password }),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendSignOutAsync(HttpClient client)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/foundation/identity/browser-session"
        );
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery"
        );
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The test antiforgery token was empty.");
    }

    private static string GetBrowserCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Single(value => value.StartsWith("vinx.session=", StringComparison.Ordinal))
            : throw new Xunit.Sdk.XunitException(
                "The response did not set the browser session cookie."
            );

    private static async Task<Guid> SeedAuthorizedCompanyAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var group = new Group(Guid.NewGuid(), "BFF group", factory.ActorUserId, Guid.NewGuid());
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "BFF company",
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var membership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Company owner",
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            membership.Id,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var capability = await administrator.Capabilities.SingleAsync(item =>
            item.Name == "foundation.employees.create"
        );
        administrator.AddRange(
            group,
            company,
            membership,
            role,
            assignment,
            new RoleCapability(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                capability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                Guid.NewGuid()
            )
        );
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static HttpResponseMessage ProviderResponse(string accessToken, string? refreshToken) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new { access_token = accessToken, refresh_token = refreshToken }
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ResponseFactory?.Invoke(request)
                    ?? throw new InvalidOperationException(
                        "The provider response was not configured."
                    )
            );
    }
}
