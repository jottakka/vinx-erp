using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
using Vinx.Erp.Foundation.Bootstrap;
using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ApiHostPlatformTests
{
    private const string StorageCredential = "test-service-role-key";
    private const string DataProtectionCredential = "test-data-protection-password";
    private const string SensitiveFailure = "sensitive-platform-failure";

    [Theory]
    [InlineData("FOUNDATION_RUNTIME_CONNECTION", "")]
    [InlineData("SUPABASE_STORAGE_URL", "not-a-uri")]
    [InlineData("SUPABASE_STORAGE_URL", "http://storage.example.test")]
    [InlineData("SUPABASE_STORAGE_SERVICE_ROLE_KEY", "")]
    [InlineData("SUPABASE_AUTH_ISSUER", "not-a-uri")]
    [InlineData("SUPABASE_AUTH_ISSUER", "http://identity.example.test/auth/v1")]
    [InlineData("SUPABASE_AUTH_AUDIENCE", "")]
    [InlineData("SUPABASE_AUTH_PUBLISHABLE_KEY", "")]
    public void Invalid_required_option_fails_host_startup_without_disclosing_credentials(
        string key,
        string value
    )
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string> { [key] = value }
        );

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
        Assert.DoesNotContain(StorageCredential, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_missing_browser_session_data_protection_setting_fails_without_disclosing_credentials()
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string>
            {
                ["BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PASSWORD"] = "",
            },
            environment: "Production"
        );

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
        Assert.DoesNotContain(
            DataProtectionCredential,
            failure.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("SUPABASE_STORAGE_URL", "http://127.0.0.1:54321")]
    [InlineData("SUPABASE_AUTH_ISSUER", "http://127.0.0.1:54321/auth/v1")]
    public void Production_rejects_plaintext_provider_endpoints(string key, string value)
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string> { [key] = value },
            environment: "Production"
        );

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    [Theory]
    [InlineData("Development", "http://127.0.0.1:54321/auth/v1", false)]
    [InlineData("Production", "https://project.supabase.co/auth/v1", true)]
    public void Jwt_metadata_requires_https_except_for_loopback_development(
        string environment,
        string issuer,
        bool requireHttpsMetadata
    )
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string> { ["SUPABASE_AUTH_ISSUER"] = issuer },
            environment
        );
        using var client = factory.CreateClient();

        var options = factory
            .Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(requireHttpsMetadata, options.RequireHttpsMetadata);
    }

    [Fact]
    public void Storage_client_disables_redirects_to_keep_service_credentials_on_configured_origin()
    {
        using var factory = new ApiHostPlatformFactory();
        using var client = factory.CreateClient();
        var handler = factory
            .Services.GetRequiredService<IHttpMessageHandlerFactory>()
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

    [Fact]
    public async Task Bootstrap_openapi_declares_bearer_authentication_and_all_public_outcomes()
    {
        using var factory = new ApiHostPlatformFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var get = document
            .RootElement.GetProperty("paths")
            .GetProperty("/foundation/bootstrap")
            .GetProperty("get");
        var responses = get.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("401", out _));
        Assert.True(responses.TryGetProperty("429", out _));
        Assert.True(responses.TryGetProperty("503", out _));
        Assert.False(responses.GetProperty("401").TryGetProperty("content", out _));
        var unavailableSchema = ResolveResponseSchema(
            document.RootElement,
            responses.GetProperty("503")
        );
        var unavailableProperties = unavailableSchema.GetProperty("properties");
        Assert.Single(unavailableProperties.EnumerateObject());
        Assert.Equal(
            "string",
            unavailableProperties.GetProperty("code").GetProperty("type").GetString()
        );
        Assert.True(get.TryGetProperty("security", out var security));
        Assert.Contains(
            security.EnumerateArray(),
            requirement => IsEmptySecurityRequirement(requirement, "Bearer")
        );
        Assert.Contains(
            security.EnumerateArray(),
            requirement => IsEmptySecurityRequirement(requirement, "BrowserSessionCookie")
        );
        var bearer = document
            .RootElement.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task Browser_session_openapi_keeps_login_anonymous_and_sign_out_cookie_only()
    {
        using var factory = new ApiHostPlatformFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var session = paths.GetProperty("/foundation/identity/browser-session");
        var antiforgery = paths
            .GetProperty("/foundation/identity/browser-session/antiforgery")
            .GetProperty("get");
        var signIn = session.GetProperty("post");
        var signOut = session.GetProperty("delete");

        Assert.False(antiforgery.TryGetProperty("security", out _));
        Assert.False(signIn.TryGetProperty("security", out _));
        foreach (var status in new[] { "200", "503" })
        {
            Assert.True(antiforgery.GetProperty("responses").TryGetProperty(status, out _));
        }

        foreach (var status in new[] { "204", "400", "401", "429", "503" })
        {
            Assert.True(signIn.GetProperty("responses").TryGetProperty(status, out _));
        }

        var signOutSecurity = signOut.GetProperty("security");
        Assert.Single(signOutSecurity.EnumerateArray());
        Assert.True(
            IsEmptySecurityRequirement(
                signOutSecurity.EnumerateArray().Single(),
                "BrowserSessionCookie"
            )
        );
        var browserCookie = document
            .RootElement.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("BrowserSessionCookie");
        Assert.Equal("apiKey", browserCookie.GetProperty("type").GetString());
        Assert.Equal("cookie", browserCookie.GetProperty("in").GetString());
        Assert.Equal("vinx.session", browserCookie.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Browser_session_openapi_declares_strict_login_body_and_csrf_headers()
    {
        using var factory = new ApiHostPlatformFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = document
            .RootElement.GetProperty("paths")
            .GetProperty("/foundation/identity/browser-session");
        var signIn = session.GetProperty("post");
        var signOut = session.GetProperty("delete");

        var requestBody = signIn.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        var schema = ResolveSchema(
            document.RootElement,
            requestBody.GetProperty("content").GetProperty("application/json").GetProperty("schema")
        );
        Assert.Equal(
            new[] { "email", "password" },
            schema
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray()
        );
        Assert.Equal(
            new[] { "email", "password" },
            schema
                .GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray()
        );
        Assert.All(
            schema.GetProperty("properties").EnumerateObject(),
            property => Assert.Equal("string", property.Value.GetProperty("type").GetString())
        );
    }

    [Fact]
    public async Task Employee_directory_openapi_declares_strict_body_opaque_responses_and_bearer_security()
    {
        using var factory = new ApiHostPlatformFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var post = document
            .RootElement.GetProperty("paths")
            .GetProperty("/foundation/companies/{companyId}/employees")
            .GetProperty("post");
        var schema = ResolveSchema(
            document.RootElement,
            post.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
        );
        Assert.Equal(
            new[] { "companyId", "displayName", "email", "requestId" },
            schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order()
        );
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        foreach (var status in new[] { "200", "201" })
        {
            var output = ResolveSchema(
                document.RootElement,
                post.GetProperty("responses")
                    .GetProperty(status)
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
            );
            Assert.Equal(
                new[] { "employeeId", "membershipId", "userId" },
                output.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order()
            );
            Assert.False(output.GetProperty("additionalProperties").GetBoolean());
        }
        var security = post.GetProperty("security");
        Assert.Contains(
            security.EnumerateArray(),
            requirement => IsEmptySecurityRequirement(requirement, "Bearer")
        );
        Assert.Contains(
            security.EnumerateArray(),
            requirement => IsEmptySecurityRequirement(requirement, "BrowserSessionCookie")
        );

        var csrfHeader = Assert.Single(
            post.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "X-CSRF-TOKEN"
        );
        Assert.Equal("header", csrfHeader.GetProperty("in").GetString());
        Assert.False(
            csrfHeader.TryGetProperty("required", out var required) && required.GetBoolean()
        );
        Assert.Equal("string", csrfHeader.GetProperty("schema").GetProperty("type").GetString());
        var csrfDescription = csrfHeader.GetProperty("description").GetString() ?? string.Empty;
        Assert.Contains(
            "BrowserSessionCookie",
            csrfDescription,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("Bearer", csrfDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("obrigat", csrfDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dispens", csrfDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browser_session_openapi_declares_required_csrf_headers_for_mutations()
    {
        using var factory = new ApiHostPlatformFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = document
            .RootElement.GetProperty("paths")
            .GetProperty("/foundation/identity/browser-session");

        AssertRequiredCsrfHeader(session.GetProperty("post"));
        AssertRequiredCsrfHeader(session.GetProperty("delete"));
    }

    [Fact]
    public async Task Bootstrap_openapi_is_not_mapped_in_production()
    {
        using var factory = new ApiHostPlatformFactory(environment: "Production");

        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Platform_operator_bootstrap_openapi_declares_bearer_authentication_and_all_public_outcomes()
    {
        using var factory = new ApiHostPlatformFactory();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var get = document
            .RootElement.GetProperty("paths")
            .GetProperty("/foundation/platform/bootstrap")
            .GetProperty("get");
        var responses = get.GetProperty("responses");
        foreach (var status in new[] { "200", "401", "403", "429", "503" })
        {
            Assert.True(
                responses.TryGetProperty(status, out _),
                $"Missing OpenAPI response {status}."
            );
        }

        Assert.False(responses.GetProperty("401").TryGetProperty("content", out _));
        Assert.Equal(
            "string",
            ResolveResponseSchema(document.RootElement, responses.GetProperty("403"))
                .GetProperty("properties")
                .GetProperty("code")
                .GetProperty("type")
                .GetString()
        );
        Assert.Equal(
            "string",
            ResolveResponseSchema(document.RootElement, responses.GetProperty("503"))
                .GetProperty("properties")
                .GetProperty("code")
                .GetProperty("type")
                .GetString()
        );
        Assert.True(get.TryGetProperty("security", out var security));
        Assert.Contains(
            security.EnumerateArray(),
            requirement =>
            {
                var schemes = requirement.EnumerateObject().ToArray();
                return schemes.Length == 1
                    && schemes[0].Name == "Bearer"
                    && schemes[0].Value.ValueKind == JsonValueKind.Array
                    && !schemes[0].Value.EnumerateArray().Any();
            }
        );
    }

    [Fact]
    public void Invalid_bootstrap_flag_configuration_fails_host_startup()
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string>
            {
                ["Foundation:Bootstrap:Flags:bad key:Enabled"] = "true",
                ["Foundation:Bootstrap:Flags:bad key:Scope"] = "release",
                ["Foundation:Bootstrap:Flags:bad key:Owner"] = "foundation",
                ["Foundation:Bootstrap:Flags:bad key:Reason"] = "test",
                ["Foundation:Bootstrap:Flags:bad key:RemoveAfter"] = "2026-12-31",
            }
        );

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    [Fact]
    public void Bootstrap_flag_configuration_with_missing_metadata_fails_host_startup()
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string>
            {
                ["Foundation:Bootstrap:Flags:release.dashboard:Enabled"] = "true",
                ["Foundation:Bootstrap:Flags:release.dashboard:Owner"] = "foundation",
                ["Foundation:Bootstrap:Flags:release.dashboard:Reason"] = "test",
                ["Foundation:Bootstrap:Flags:release.dashboard:RemoveAfter"] = "2026-12-31",
            }
        );

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    [Fact]
    public void Bootstrap_flag_configuration_with_missing_enabled_fails_host_startup()
    {
        using var factory = new ApiHostPlatformFactory(
            new Dictionary<string, string>
            {
                ["Foundation:Bootstrap:Flags:release.dashboard:Scope"] = "release",
                ["Foundation:Bootstrap:Flags:release.dashboard:Owner"] = "foundation",
                ["Foundation:Bootstrap:Flags:release.dashboard:Reason"] = "test",
                ["Foundation:Bootstrap:Flags:release.dashboard:RemoveAfter"] = "2026-12-31",
            }
        );

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Reason")]
    [InlineData("RemoveAfter")]
    public void Bootstrap_flag_configuration_with_each_required_metadata_field_missing_fails_host_startup(
        string missingField
    )
    {
        var settings = ValidFlagConfiguration();
        settings.Remove($"Foundation:Bootstrap:Flags:release.dashboard:{missingField}");
        using var factory = new ApiHostPlatformFactory(settings);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    [Fact]
    public void Bootstrap_feature_flags_reject_casing_only_duplicate_keys()
    {
        var options = new FoundationBootstrapFeatureFlagsOptions
        {
            Flags = new Dictionary<string, FoundationBootstrapFeatureFlagDefinitionOptions>(
                StringComparer.Ordinal
            )
            {
                ["release.dashboard"] = ValidFlag(enabled: true),
                ["RELEASE.dashboard"] = ValidFlag(enabled: false),
            },
        };

        Assert.False(options.IsValid(out var failure));
        Assert.Equal(
            "Foundation bootstrap feature flag keys must be unique without casing differences.",
            failure
        );
    }

    [Fact]
    public void Bootstrap_feature_flags_reject_a_unique_noncanonical_key()
    {
        var options = new FoundationBootstrapFeatureFlagsOptions
        {
            Flags = new Dictionary<string, FoundationBootstrapFeatureFlagDefinitionOptions>(
                StringComparer.Ordinal
            )
            {
                ["RELEASE.dashboard"] = ValidFlag(enabled: true),
            },
        };

        Assert.False(options.IsValid(out var failure));
        Assert.Equal("Foundation bootstrap feature flag key is invalid.", failure);
    }

    private static FoundationBootstrapFeatureFlagDefinitionOptions ValidFlag(bool enabled) =>
        new()
        {
            Enabled = enabled,
            Scope = "release",
            Owner = "foundation",
            Reason = "test",
            RemoveAfter = new DateOnly(2026, 12, 31),
        };

    private static Dictionary<string, string> ValidFlagConfiguration() =>
        new()
        {
            ["Foundation:Bootstrap:Flags:release.dashboard:Enabled"] = "true",
            ["Foundation:Bootstrap:Flags:release.dashboard:Scope"] = "release",
            ["Foundation:Bootstrap:Flags:release.dashboard:Owner"] = "foundation",
            ["Foundation:Bootstrap:Flags:release.dashboard:Reason"] = "test",
            ["Foundation:Bootstrap:Flags:release.dashboard:RemoveAfter"] = "2026-12-31",
        };

    private static JsonElement ResolveResponseSchema(JsonElement document, JsonElement response)
    {
        var schema = response
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        var schemaName =
            reference.GetString()?.Split('/').Last()
            ?? throw new InvalidOperationException("The 503 response schema reference was empty.");
        return document.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
    }

    private static JsonElement ResolveSchema(JsonElement document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        var schemaName =
            reference.GetString()?.Split('/').Last()
            ?? throw new InvalidOperationException("The request schema reference was empty.");
        return document.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
    }

    private static void AssertRequiredCsrfHeader(JsonElement operation)
    {
        var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray());
        Assert.Equal("X-CSRF-TOKEN", parameter.GetProperty("name").GetString());
        Assert.Equal("header", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
    }

    private static bool IsEmptySecurityRequirement(JsonElement requirement, string schemeName)
    {
        var schemes = requirement.EnumerateObject().ToArray();
        return schemes.Length == 1
            && schemes[0].Name == schemeName
            && schemes[0].Value.ValueKind == JsonValueKind.Array
            && !schemes[0].Value.EnumerateArray().Any();
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
        private string? _dataProtectionKeyDirectory;
        private string? _dataProtectionCertificatePath;

        public ApiHostPlatformFactory(
            IReadOnlyDictionary<string, string>? overrides = null,
            string environment = "Development",
            bool exposeFailureEndpoint = false,
            bool exposeRateLimitedEndpoint = false
        )
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
                "Host=127.0.0.1;Port=1;Database=vinx;Username=vinx;Password=not-used"
            );
            builder.UseSetting(
                "SUPABASE_STORAGE_URL",
                _environment == "Production"
                    ? "https://storage.example.test"
                    : "http://127.0.0.1:54321"
            );
            builder.UseSetting("SUPABASE_STORAGE_SERVICE_ROLE_KEY", StorageCredential);
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.co/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
            builder.UseSetting("SUPABASE_AUTH_PUBLISHABLE_KEY", "test-publishable-key");
            if (string.Equals(_environment, "Production", StringComparison.Ordinal))
            {
                ConfigureProductionDataProtection(builder);
            }

            foreach (var setting in _overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            if (_exposeFailureEndpoint)
            {
                builder.ConfigureTestServices(services =>
                    services.AddTransient<IStartupFilter, PlatformFailureStartupFilter>()
                );
            }

            if (_exposeRateLimitedEndpoint)
            {
                builder.ConfigureTestServices(services =>
                    services.AddTransient<IStartupFilter, PlatformRateLimitStartupFilter>()
                );
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_dataProtectionCertificatePath is not null)
                {
                    if (File.Exists(_dataProtectionCertificatePath))
                    {
                        File.Delete(_dataProtectionCertificatePath);
                    }

                    _dataProtectionCertificatePath = null;
                }

                if (_dataProtectionKeyDirectory is not null)
                {
                    if (Directory.Exists(_dataProtectionKeyDirectory))
                    {
                        Directory.Delete(_dataProtectionKeyDirectory, recursive: true);
                    }

                    _dataProtectionKeyDirectory = null;
                }
            }

            base.Dispose(disposing);
        }

        private void ConfigureProductionDataProtection(IWebHostBuilder builder)
        {
            _dataProtectionKeyDirectory = Path.Combine(
                Path.GetTempPath(),
                $"vinx-erp-data-protection-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(_dataProtectionKeyDirectory);

            _dataProtectionCertificatePath = Path.Combine(
                _dataProtectionKeyDirectory,
                "certificate.pfx"
            );
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Vinx ERP integration test",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1)
            );
            File.WriteAllBytes(
                _dataProtectionCertificatePath,
                certificate.Export(X509ContentType.Pfx, DataProtectionCredential)
            );

            builder.UseSetting(
                "BROWSER_SESSION_DATA_PROTECTION_KEY_DIRECTORY",
                _dataProtectionKeyDirectory
            );
            builder.UseSetting(
                "BROWSER_SESSION_DATA_PROTECTION_APPLICATION_NAME",
                "vinx-erp-integration-tests"
            );
            builder.UseSetting(
                "BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PATH",
                _dataProtectionCertificatePath
            );
            builder.UseSetting(
                "BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PASSWORD",
                DataProtectionCredential
            );
        }
    }

    private sealed class PlatformFailureStartupFilter : IStartupFilter
    {
        private static readonly Endpoint FailureEndpoint = new(
            _ => throw new InvalidOperationException(SensitiveFailure),
            new EndpointMetadataCollection(new AuthorizeAttribute(), new AllowAnonymousAttribute()),
            "Test platform failure endpoint"
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

    private sealed class PlatformRateLimitStartupFilter : IStartupFilter
    {
        private static readonly Endpoint RateLimitedEndpoint = new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("foundation")),
            "Test rate-limited endpoint"
        );

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        if (context.Request.Path == "/test/platform/rate-limit")
                        {
                            context.SetEndpoint(RateLimitedEndpoint);
                        }

                        return nextMiddleware();
                    }
                );
                next(app);
            };
    }
}
