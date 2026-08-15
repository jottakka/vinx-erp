using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ApiHostPlatformTests
{
    private const string StorageCredential = "test-service-role-key";
    private const string SensitiveFailure = "sensitive-platform-failure";

    [Theory]
    [InlineData("FOUNDATION_RUNTIME_CONNECTION", "")]
    [InlineData("SUPABASE_STORAGE_URL", "not-a-uri")]
    [InlineData("SUPABASE_STORAGE_URL", "http://storage.example.test")]
    [InlineData("SUPABASE_STORAGE_SERVICE_ROLE_KEY", "")]
    [InlineData("SUPABASE_AUTH_ISSUER", "not-a-uri")]
    [InlineData("SUPABASE_AUTH_ISSUER", "http://identity.example.test/auth/v1")]
    [InlineData("SUPABASE_AUTH_AUDIENCE", "")]
    public void Invalid_required_option_fails_host_startup_without_disclosing_credentials(
        string key,
        string value)
    {
        using var factory = new ApiHostPlatformFactory(new Dictionary<string, string>
        {
            [key] = value
        });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
        Assert.DoesNotContain(StorageCredential, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SUPABASE_STORAGE_URL", "http://127.0.0.1:54321")]
    [InlineData("SUPABASE_AUTH_ISSUER", "http://127.0.0.1:54321/auth/v1")]
    public void Production_rejects_plaintext_provider_endpoints(string key, string value)
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string> { [key] = value },
            environment: "Production");

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    [Theory]
    [InlineData("Development", "http://127.0.0.1:54321/auth/v1", false)]
    [InlineData("Production", "https://project.supabase.co/auth/v1", true)]
    public void Jwt_metadata_requires_https_except_for_loopback_development(
        string environment,
        string issuer,
        bool requireHttpsMetadata)
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string> { ["SUPABASE_AUTH_ISSUER"] = issuer },
            environment);
        using var client = factory.CreateClient();

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(requireHttpsMetadata, options.RequireHttpsMetadata);
    }

    [Fact]
    public void Storage_client_disables_redirects_to_keep_service_credentials_on_configured_origin()
    {
        using var factory = new ApiHostPlatformFactory();
        using var client = factory.CreateClient();
        var handler = factory.Services
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(SupabaseFileStorage));

        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler!;
        }

        var primaryHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primaryHandler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Unhandled_failure_returns_sanitized_problem_details()
    {
        using var factory = new ApiHostPlatformFactory(exposeFailureEndpoint: true);

        using var response = await factory.CreateClient().GetAsync("/test/platform/failure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(SensitiveFailure, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_requests_use_a_bounded_partition_without_limiting_health()
    {
        using var factory = new ApiHostPlatformFactory();
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await client.GetAsync("/foundation/capabilities");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await client.GetAsync("/foundation/capabilities");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Rate_limiter_bounds_accepted_requests_without_limiting_health()
    {
        using var factory = new ApiHostPlatformFactory(exposeRateLimitedEndpoint: true);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await client.GetAsync("/test/platform/rate-limit");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using var limited = await client.GetAsync("/test/platform/rate-limit");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static TException? FindException<TException>(Exception failure)
        where TException : Exception
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is TException expected)
            {
                return expected;
            }
        }

        return null;
    }

    private sealed class ApiHostPlatformFactory : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string> _overrides;
        private readonly string _environment;
        private readonly bool _exposeFailureEndpoint;
        private readonly bool _exposeRateLimitedEndpoint;

        public ApiHostPlatformFactory(
            IReadOnlyDictionary<string, string>? overrides = null,
            string environment = "Development",
            bool exposeFailureEndpoint = false,
            bool exposeRateLimitedEndpoint = false)
        {
            _overrides = overrides ?? new Dictionary<string, string>();
            _environment = environment;
            _exposeFailureEndpoint = exposeFailureEndpoint;
            _exposeRateLimitedEndpoint = exposeRateLimitedEndpoint;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
            builder.UseSetting(
                "FOUNDATION_RUNTIME_CONNECTION",
                "Host=127.0.0.1;Port=1;Database=vinx;Username=vinx;Password=not-used");
            builder.UseSetting(
                "SUPABASE_STORAGE_URL",
                _environment == "Production" ? "https://storage.example.test" : "http://127.0.0.1:54321");
            builder.UseSetting("SUPABASE_STORAGE_SERVICE_ROLE_KEY", StorageCredential);
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.co/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");

            foreach (var setting in _overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            if (_exposeFailureEndpoint)
            {
                builder.ConfigureTestServices(services =>
                    services.AddTransient<IStartupFilter, PlatformFailureStartupFilter>());
            }

            if (_exposeRateLimitedEndpoint)
            {
                builder.ConfigureTestServices(services =>
                    services.AddTransient<IStartupFilter, PlatformRateLimitStartupFilter>());
            }
        }
    }

    private sealed class PlatformFailureStartupFilter : IStartupFilter
    {
        private static readonly Endpoint FailureEndpoint = new(
            _ => throw new InvalidOperationException(SensitiveFailure),
            new EndpointMetadataCollection(new AuthorizeAttribute(), new AllowAnonymousAttribute()),
            "Test platform failure endpoint");

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Path == "/test/platform/failure")
                {
                    context.SetEndpoint(FailureEndpoint);
                }

                return nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class PlatformRateLimitStartupFilter : IStartupFilter
    {
        private static readonly Endpoint RateLimitedEndpoint = new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("foundation")),
            "Test rate-limited endpoint");

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Path == "/test/platform/rate-limit")
                {
                    context.SetEndpoint(RateLimitedEndpoint);
                }

                return nextMiddleware();
            });
            next(app);
        };
    }
}
