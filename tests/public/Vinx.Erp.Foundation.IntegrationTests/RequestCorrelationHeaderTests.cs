using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Wolverine;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RequestCorrelationHeaderTests
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string SensitiveFailure = "sensitive-platform-failure";

    [Fact]
    public async Task Successful_response_contains_a_server_created_correlation_id()
    {
        using var factory = new RequestCorrelationApiFactory();

        using var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertValidCorrelationId(response);
    }

    [Fact]
    public async Task Correlation_id_uses_the_canonical_lowercase_d_guid_format()
    {
        using var factory = new RequestCorrelationApiFactory();

        using var response = await factory.CreateClient().GetAsync("/health");

        var value = AssertCorrelationHeaderValue(response);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", value);
    }

    [Fact]
    public async Task No_content_response_contains_a_server_created_correlation_id()
    {
        using var factory = new RequestCorrelationApiFactory(exposeNoContentEndpoint: true);

        using var response = await factory.CreateClient().GetAsync("/test/no-content");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertValidCorrelationId(response);
    }

    [Fact]
    public async Task Response_header_matches_the_current_server_correlation_scope()
    {
        using var factory = new RequestCorrelationApiFactory(exposeScopeEndpoint: true);

        using var response = await factory.CreateClient().GetAsync("/test/correlation-scope");

        var responseCorrelationId = AssertValidCorrelationId(response);
        var scopeCorrelationId = Guid.Parse(
            response.Headers.GetValues("X-Test-Correlation-Scope").Single());
        Assert.Equal(scopeCorrelationId, responseCorrelationId);
    }

    [Fact]
    public async Task Unauthorized_response_contains_a_server_created_correlation_id()
    {
        using var factory = new RequestCorrelationApiFactory();

        using var response = await factory.CreateClient().GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertValidCorrelationId(response);
    }

    [Fact]
    public async Task Exception_response_contains_a_server_created_correlation_id_without_sensitive_details()
    {
        using var factory = new RequestCorrelationApiFactory(exposeFailureEndpoint: true);

        using var response = await factory.CreateClient().GetAsync("/test/platform/failure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(SensitiveFailure, body, StringComparison.Ordinal);
        AssertValidCorrelationId(response);
    }

    [Fact]
    public async Task Identity_gate_unavailable_response_contains_a_server_created_correlation_id()
    {
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndDatabaseFailureAsync(
                GateDatabaseFailurePoint.Resolution
            );
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid));

        using var response = await client.GetAsync("/foundation/bootstrap");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("identity_gate_unavailable", document.RootElement.GetProperty("code").GetString());
        AssertValidCorrelationId(response);
    }

    [Fact]
    public async Task Bootstrap_openapi_declares_correlation_id_as_a_response_header()
    {
        using var factory = new RequestCorrelationApiFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/foundation/bootstrap")
            .GetProperty("get");
        var responses = operation.GetProperty("responses");

        if (operation.TryGetProperty("parameters", out var parameters))
        {
            Assert.DoesNotContain(
                parameters.EnumerateArray(),
                parameter => parameter.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), CorrelationHeader, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var status in new[] { "200", "401", "429", "503" })
        {
            var responseMetadata = responses.GetProperty(status);
            Assert.True(
                responseMetadata.TryGetProperty("headers", out var headers),
                $"Expected {CorrelationHeader} response header metadata for HTTP {status}.");
            var correlationHeader = headers.GetProperty(CorrelationHeader);
            Assert.Equal("string", correlationHeader.GetProperty("schema").GetProperty("type").GetString());
            Assert.Equal("uuid", correlationHeader.GetProperty("schema").GetProperty("format").GetString());
            Assert.True(correlationHeader.GetProperty("required").GetBoolean());
        }
    }

    [Fact]
    public async Task Forged_incoming_correlation_id_is_ignored()
    {
        using var factory = new RequestCorrelationApiFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        var forged = Guid.NewGuid();
        request.Headers.Add(CorrelationHeader, forged.ToString());

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = AssertValidCorrelationId(response);
        Assert.NotEqual(forged, actual);
    }

    [Fact]
    public async Task Concurrent_responses_contain_distinct_server_created_correlation_ids()
    {
        using var factory = new RequestCorrelationApiFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => client.GetAsync("/health"))
        );

        try
        {
            Assert.All(responses, response =>
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                AssertValidCorrelationId(response);
            });

            var correlationIds = responses.Select(GetCorrelationId).ToArray();
            Assert.Equal(correlationIds.Length, correlationIds.Distinct().Count());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static Guid AssertValidCorrelationId(HttpResponseMessage response)
    {
        Assert.True(
            response.Headers.TryGetValues(CorrelationHeader, out var values),
            $"Expected {CorrelationHeader} response header.");
        var value = Assert.Single(values!);
        Assert.True(Guid.TryParseExact(value, "D", out var correlationId), $"Invalid correlation ID: {value}");
        Assert.NotEqual(Guid.Empty, correlationId);
        Assert.Equal(correlationId.ToString("D"), value);
        return correlationId;
    }

    private static string AssertCorrelationHeaderValue(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues(CorrelationHeader));

    private static Guid GetCorrelationId(HttpResponseMessage response) =>
        AssertValidCorrelationId(response);

    private sealed class RequestCorrelationApiFactory(
        bool exposeFailureEndpoint = false,
        bool exposeNoContentEndpoint = false,
        bool exposeScopeEndpoint = false) : WebApplicationFactory<Program>
    {
        private readonly bool _exposeFailureEndpoint = exposeFailureEndpoint;
        private readonly bool _exposeNoContentEndpoint = exposeNoContentEndpoint;
        private readonly bool _exposeScopeEndpoint = exposeScopeEndpoint;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(
                "FOUNDATION_RUNTIME_CONNECTION",
                "Host=127.0.0.1;Port=1;Database=vinx;Username=vinx;Password=not-used"
            );
            builder.UseSetting("SUPABASE_STORAGE_URL", "http://127.0.0.1:54321");
            builder.UseSetting("SUPABASE_STORAGE_SERVICE_ROLE_KEY", "test-service-role-key");
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.co/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
            builder.UseSetting("SUPABASE_AUTH_PUBLISHABLE_KEY", "test-publishable-key");

            builder.ConfigureTestServices(services =>
            {
                services.DisableAllWolverineMessagePersistence();
                services.DisableAllExternalWolverineTransports();
                if (_exposeFailureEndpoint)
                {
                    services.AddTransient<IStartupFilter, FailureEndpointStartupFilter>();
                }

                if (_exposeNoContentEndpoint)
                {
                    services.AddTransient<IStartupFilter, NoContentEndpointStartupFilter>();
                }

                if (_exposeScopeEndpoint)
                {
                    services.AddTransient<IStartupFilter, CorrelationScopeEndpointStartupFilter>();
                }
            });
        }
    }

    private sealed class NoContentEndpointStartupFilter : IStartupFilter
    {
        private static readonly Endpoint NoContentEndpoint = new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            "Test no-content response endpoint"
        );

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        if (context.Request.Path == "/test/no-content")
                        {
                            context.SetEndpoint(NoContentEndpoint);
                        }

                        return nextMiddleware();
                    }
                );
                next(app);
            };
    }

    private sealed class CorrelationScopeEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        if (context.Request.Path == "/test/correlation-scope")
                        {
                            context.Response.OnStarting(() =>
                            {
                                context.Response.Headers["X-Test-Correlation-Scope"] =
                                    ServerCorrelationContext.Get(context).CorrelationId.ToString("D");
                                return Task.CompletedTask;
                            });
                        }

                        return nextMiddleware();
                    }
                );
                next(app);
            };
    }

    private sealed class FailureEndpointStartupFilter : IStartupFilter
    {
        private static readonly Endpoint FailureEndpoint = new(
            _ => throw new InvalidOperationException(SensitiveFailure),
            new EndpointMetadataCollection(new AuthorizeAttribute(), new AllowAnonymousAttribute()),
            "Test request correlation failure endpoint"
        );

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        if (context.Request.Path == "/test/platform/failure")
                        {
                            context.SetEndpoint(FailureEndpoint);
                        }

                        return nextMiddleware();
                    }
                );
                next(app);
            };
    }
}
