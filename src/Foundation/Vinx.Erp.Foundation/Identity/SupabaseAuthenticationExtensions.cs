using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vinx.Erp.Foundation.Identity;

public sealed class SupabaseAuthOptions
{
    public const string IssuerConfigurationKey = "SUPABASE_AUTH_ISSUER";
    public const string AudienceConfigurationKey = "SUPABASE_AUTH_AUDIENCE";

    [ConfigurationKeyName(IssuerConfigurationKey)]
    public string Issuer { get; set; } = string.Empty;

    [ConfigurationKeyName(AudienceConfigurationKey)]
    public string Audience { get; set; } = string.Empty;

    public Uri IssuerUri => new(Issuer, UriKind.Absolute);

    public string NormalizedIssuer => IssuerUri.AbsoluteUri.TrimEnd('/');
}

public static class SupabaseAuthenticationExtensions
{
    /// <summary>
    /// Configura a validação nativa do JWT emitido pelo Supabase para uso compartilhado pelos hosts.
    /// </summary>
    public static IServiceCollection AddSupabaseJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<SupabaseAuthOptions>()
            .Bind(configuration)
            .Validate(
                options => IsAllowedProviderUri(options.Issuer, environment),
                "Supabase Auth issuer must use HTTPS, except loopback HTTP in Development.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "Supabase Auth audience must be configured.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<SupabaseAuthOptions>>((options, configuredOptions) =>
            {
                var auth = configuredOptions.Value;
                options.Authority = auth.NormalizedIssuer;
                options.RequireHttpsMetadata = auth.IssuerUri.Scheme == Uri.UriSchemeHttps;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidIssuer = auth.NormalizedIssuer;
                options.TokenValidationParameters.ValidAudience = auth.Audience;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
            });
        services.AddAuthorization();
        return services;
    }

    private static bool IsAllowedProviderUri(string value, IHostEnvironment environment) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
