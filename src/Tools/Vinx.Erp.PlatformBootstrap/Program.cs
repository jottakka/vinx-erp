using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.PlatformBootstrap;

return await PlatformBootstrapCommand.RunAsync(args);

internal static class PlatformBootstrapCommand
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 0)
        {
            return await WriteOutcomeAsync("configuration_invalid", exitCode: 1);
        }

        try
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.Logging.ClearProviders();
            builder
                .Services.AddOptions<PlatformBootstrapDatabaseOptions>()
                .Bind(builder.Configuration)
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                    "Foundation migrator connection must be configured."
                );
            builder
                .Services.AddOptions<PlatformOperatorDeploymentOptions>()
                .Bind(builder.Configuration)
                .Validate(
                    options =>
                        !string.IsNullOrWhiteSpace(options.Subject)
                        && options.Subject.Trim().Length <= 400
                        && !string.IsNullOrWhiteSpace(options.DisplayName)
                        && options.DisplayName.Trim().Length <= 200,
                    "Platform operator deployment identity is invalid."
                );
            builder
                .Services.AddOptions<SupabaseAuthOptions>()
                .Bind(builder.Configuration)
                .Validate(
                    options => IsAllowedIssuer(options.Issuer, builder.Environment),
                    "Supabase Auth issuer is invalid."
                )
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.Audience),
                    "Supabase Auth audience must be configured."
                );
            builder
                .Services.AddOptions<SupabaseAdminOptions>()
                .Bind(builder.Configuration)
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.ServiceRoleKey),
                    "Supabase Admin service role key must be configured."
                );
            builder.Services.AddSingleton(serviceProvider =>
                serviceProvider.GetRequiredService<IOptions<SupabaseAuthOptions>>().Value
            );
            builder.Services.AddSingleton(serviceProvider =>
                serviceProvider.GetRequiredService<IOptions<SupabaseAdminOptions>>().Value
            );
            builder.Services.AddDbContext<FoundationDbContext>(
                (serviceProvider, options) =>
                    options
                        .UseNpgsql(
                            serviceProvider
                                .GetRequiredService<IOptions<PlatformBootstrapDatabaseOptions>>()
                                .Value.ConnectionString
                        )
                        .UseSnakeCaseNamingConvention()
                        .AddInterceptors(new RowVersionInterceptor())
            );
            builder
                .Services.AddHttpClient<SupabaseAdminUserVerifier>()
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new SocketsHttpHandler { AllowAutoRedirect = false }
                );
            builder.Services.AddScoped<PlatformOperatorBootstrapService>();
            builder.Services.AddScoped<PlatformBootstrapRunner>();

            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var outcome = await scope
                .ServiceProvider.GetRequiredService<PlatformBootstrapRunner>()
                .RunAsync(CancellationToken.None);
            return outcome switch
            {
                PlatformBootstrapOutcome.Created => await WriteOutcomeAsync("created", exitCode: 0),
                PlatformBootstrapOutcome.AlreadyConfigured => await WriteOutcomeAsync(
                    "already_configured",
                    exitCode: 0
                ),
                PlatformBootstrapOutcome.Conflict => await WriteOutcomeAsync(
                    "conflict",
                    exitCode: 1
                ),
                PlatformBootstrapOutcome.ProviderRejected => await WriteOutcomeAsync(
                    "provider_rejected",
                    exitCode: 1
                ),
                _ => await WriteOutcomeAsync("unavailable", exitCode: 1),
            };
        }
        catch (OptionsValidationException)
        {
            return await WriteOutcomeAsync("configuration_invalid", exitCode: 1);
        }
        catch (OperationCanceledException)
        {
            return await WriteOutcomeAsync("unavailable", exitCode: 1);
        }
        catch
        {
            return await WriteOutcomeAsync("unavailable", exitCode: 1);
        }
    }

    private static bool IsAllowedIssuer(string value, IHostEnvironment environment) =>
        Uri.TryCreate(value, UriKind.Absolute, out var issuer)
        && (
            issuer.Scheme == Uri.UriSchemeHttps
            || (
                environment.IsDevelopment()
                && issuer.Scheme == Uri.UriSchemeHttp
                && issuer.IsLoopback
            )
        );

    private static async Task<int> WriteOutcomeAsync(string value, int exitCode)
    {
        await Console.Out.WriteLineAsync(value);
        return exitCode;
    }
}
