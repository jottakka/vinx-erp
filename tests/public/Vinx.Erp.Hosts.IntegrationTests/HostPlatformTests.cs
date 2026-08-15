extern alias workerhost;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkerProgram = workerhost::Program;
using Xunit;

namespace Vinx.Erp.Hosts.IntegrationTests;

public sealed class HostPlatformTests
{
    private const string TestAuthenticationHeader = "X-Vinx-Test-Authentication";

    [Fact]
    public async Task Mcp_and_worker_hosts_expose_native_uncached_health()
    {
        using var mcpFactory = new McpHostFactory();
        using var workerFactory = new WorkerHostFactory();

        using var mcpHealth = await mcpFactory.CreateClient().GetAsync("/health");
        using var workerHealth = await workerFactory.CreateClient().GetAsync("/health");

        await AssertHealthyAsync(mcpHealth);
        await AssertHealthyAsync(workerHealth);
    }

    [Fact]
    public async Task Mcp_requires_authentication()
    {
        using var factory = new McpHostFactory();
        using var response = await PostMcpAsync(factory.CreateClient());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Official_client_lists_and_calls_only_the_stateless_ping_tool()
    {
        using var factory = new McpHostFactory(useTestAuthentication: true);
        using var httpClient = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    [TestAuthenticationHeader] = "authenticated"
                }
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal("vinx_ping", tool.Name);
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint);
        Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);

        var result = await client.CallToolAsync("vinx_ping");
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("pong", content.Text);

        var repeatedResult = await client.CallToolAsync("vinx_ping");
        var repeatedContent = Assert.IsType<TextContentBlock>(Assert.Single(repeatedResult.Content));
        Assert.Equal("pong", repeatedContent.Text);
        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task Mcp_rejects_every_browser_origin()
    {
        using var factory = new McpHostFactory(useTestAuthentication: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHeader, "authenticated");
        client.DefaultRequestHeaders.Add("Origin", "https://app.example.test");

        using var response = await PostMcpAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_rejects_untrusted_host_headers()
    {
        using var factory = new McpHostFactory(useTestAuthentication: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHeader, "authenticated");
        client.DefaultRequestHeaders.Host = "evil.example";

        using var response = await PostMcpAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_separates_anonymous_and_authenticated_rate_limits_and_keeps_health_available()
    {
        using var factory = new McpHostFactory(useTestAuthentication: true);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await PostMcpAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var anonymousLimited = await PostMcpAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, anonymousLimited.StatusCode);

        client.DefaultRequestHeaders.Add(TestAuthenticationHeader, "authenticated");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await PostMcpAsync(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await PostMcpAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static Task<HttpResponseMessage> PostMcpAsync(HttpClient client) => client.PostAsync(
        "/mcp",
        new StringContent("{}", Encoding.UTF8, new MediaTypeHeaderValue("application/json")));

    private static async Task AssertHealthyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private sealed class McpHostFactory(bool useTestAuthentication = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.co/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
            if (useTestAuthentication)
            {
                builder.ConfigureTestServices(services =>
                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { }));
            }
        }
    }

    private sealed class WorkerHostFactory : WebApplicationFactory<WorkerProgram>;

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "McpTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(TestAuthenticationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "mcp-test-client")],
                SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
