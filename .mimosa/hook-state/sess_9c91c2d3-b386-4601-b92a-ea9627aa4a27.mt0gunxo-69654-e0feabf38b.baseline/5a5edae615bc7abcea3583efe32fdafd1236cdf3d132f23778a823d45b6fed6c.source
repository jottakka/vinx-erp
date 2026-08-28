using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.Identity;

internal static class BrowserBffAuthenticationExtensions
{
    public const string CookieScheme = "BrowserSessionCookie";
    public const string PolicyScheme = "BrowserOrBearer";
    public const string BrowserSessionPolicyName = "browser.session";
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
    public const string LoginRateLimitPolicyName = "browser-session-login";

    public static string GetSessionCookieName(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() ? "vinx.session" : "__Host-vinx.session";
    }

    public static IServiceCollection AddBrowserBffSession(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<SupabaseBrowserSessionOptions>()
            .Bind(configuration)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PublishableKey),
                "Supabase Auth publishable key must be configured for browser sessions.")
            .ValidateOnStart();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<SupabaseBrowserSessionOptions>>().Value);

        services.AddOptions<BrowserBffSessionProtectionOptions>()
            .Bind(configuration)
            .Validate(
                options => environment.IsDevelopment() || options.HasProductionKeyRingConfiguration(),
                "Browser session Data Protection requires a key directory, application name and certificate outside Development.")
            .ValidateOnStart();

        ConfigureDataProtection(services, configuration, environment);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = environment.IsDevelopment()
                ? "vinx.antiforgery"
                : "__Host-vinx.antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.None
                : CookieSecurePolicy.Always;
        });

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = PolicyScheme;
                options.DefaultChallengeScheme = PolicyScheme;
                options.DefaultForbidScheme = PolicyScheme;
            })
            .AddPolicyScheme(PolicyScheme, "Browser cookie or bearer token", options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(HeaderNames.Authorization)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : CookieScheme)
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = GetSessionCookieName(environment);
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.None
                    : CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization(options =>
            options.AddPolicy(
                BrowserSessionPolicyName,
                policy => policy.RequireAuthenticatedUser().AddRequirements(new BrowserBffSessionRequirement())));
        services.AddSingleton<IAuthorizationHandler, BrowserBffSessionAuthorizationHandler>();

        services.AddHttpClient<SupabasePasswordSessionClient>((serviceProvider, client) =>
            {
                var authentication = serviceProvider
                    .GetRequiredService<IOptions<SupabaseAuthOptions>>().Value;
                client.BaseAddress = new Uri(authentication.NormalizedIssuer + "/", UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            });
        services.AddScoped(serviceProvider => new SupabaseAccessTokenValidator(
            serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme)));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<BrowserBffSessionSignInService>();
        return services;
    }

    private static void ConfigureDataProtection(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var dataProtection = services.AddDataProtection();
        if (environment.IsDevelopment())
        {
            dataProtection.SetApplicationName("vinx.erp.browser-session.development");
            return;
        }

        var keyDirectory = configuration[BrowserBffSessionProtectionOptions.KeyDirectoryConfigurationKey];
        var applicationName = configuration[BrowserBffSessionProtectionOptions.ApplicationNameConfigurationKey];
        var certificatePath = configuration[BrowserBffSessionProtectionOptions.CertificatePathConfigurationKey];
        var certificatePassword = configuration[BrowserBffSessionProtectionOptions.CertificatePasswordConfigurationKey];
        if (string.IsNullOrWhiteSpace(keyDirectory)
            || string.IsNullOrWhiteSpace(applicationName)
            || string.IsNullOrWhiteSpace(certificatePath)
            || string.IsNullOrWhiteSpace(certificatePassword))
        {
            return;
        }

        dataProtection
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
            .SetApplicationName(applicationName)
            .ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                certificatePassword));
    }
}

internal sealed class BrowserBffSessionRequirement : IAuthorizationRequirement;

internal sealed class BrowserBffSessionAuthorizationHandler
    : AuthorizationHandler<BrowserBffSessionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BrowserBffSessionRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext
            && !httpContext.Request.Headers.ContainsKey(HeaderNames.Authorization)
            && context.User.Identities.Any(identity =>
                string.Equals(identity.AuthenticationType, BrowserBffAuthenticationExtensions.CookieScheme, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class BrowserBffAntiforgeryRequiredMetadata(bool cookieAuthenticatedOnly = false)
    : Attribute, IAntiforgeryRequiredMetadata
{
    public bool CookieAuthenticatedOnly { get; } = cookieAuthenticatedOnly;
}

internal sealed class BrowserBffCookieOnlyEndpointMetadata;
