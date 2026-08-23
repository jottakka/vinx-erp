using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Configuration;
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
        if (arguments is ["wolverine-grants"])
        {
            return await RunWolverineGrantsAsync();
        }

        var isReprovisioning = arguments is ["reprovision-identity"];
        if (arguments.Length != 0 && !isReprovisioning)
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
                .Services.AddOptions<ExternalIdentityReprovisioningOptions>()
                .Bind(builder.Configuration)
                .Validate(
                    options =>
                        Guid.TryParse(options.UserId, out var userId)
                        && userId != Guid.Empty
                        && Guid.TryParse(options.ExpectedExternalIdentityId, out var identityId)
                        && identityId != Guid.Empty
                        && IsExactSubject(options.ExpectedOldSubject)
                        && IsExactSubject(options.NewSubject),
                    "External identity reprovisioning configuration is invalid."
                );
            builder
                .Services.AddOptions<SupabaseAuthOptions>()
                .Bind(builder.Configuration)
                .Validate(
                    options => AllowedUriPolicy.IsAllowedHttp(options.Issuer, builder.Environment),
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
            builder.Services.AddScoped<ExternalIdentityReprovisioningService>();
            builder.Services.AddScoped<ExternalIdentityReprovisioningRunner>();

            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            if (isReprovisioning)
            {
                var reprovisioningOutcome = await scope
                    .ServiceProvider.GetRequiredService<ExternalIdentityReprovisioningRunner>()
                    .RunAsync(CancellationToken.None);
                return reprovisioningOutcome switch
                {
                    ExternalIdentityReprovisioningState.Reprovisioned => await WriteOutcomeAsync(
                        "reprovisioned",
                        exitCode: 0
                    ),
                    ExternalIdentityReprovisioningState.AlreadyConfigured => await WriteOutcomeAsync(
                        "already_configured",
                        exitCode: 0
                    ),
                    ExternalIdentityReprovisioningState.ProviderNotDeleted => await WriteOutcomeAsync(
                        "provider_not_deleted",
                        exitCode: 1
                    ),
                    ExternalIdentityReprovisioningState.ProviderRejected => await WriteOutcomeAsync(
                        "provider_rejected",
                        exitCode: 1
                    ),
                    ExternalIdentityReprovisioningState.EmailMismatch => await WriteOutcomeAsync(
                        "email_mismatch",
                        exitCode: 1
                    ),
                    ExternalIdentityReprovisioningState.TupleMismatch => await WriteOutcomeAsync(
                        "tuple_mismatch",
                        exitCode: 1
                    ),
                    _ => await WriteOutcomeAsync("unavailable", exitCode: 1),
                };
            }

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

    /// <summary>
    /// Aplica os grants técnicos do Wolverine por Npgsql, sem expor a connection
    /// string (nem a senha) em argumentos de processo.
    /// </summary>
    private static async Task<int> RunWolverineGrantsAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "FOUNDATION_MIGRATOR_CONNECTION"
        );
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await WriteOutcomeAsync("configuration_invalid", exitCode: 1);
        }

        var repositoryRoot = FindRepositoryRoot();
        var grantsSql = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "infra", "database", "grant-wolverine-runtime.sql")
        );

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(grantsSql, connection);
            await command.ExecuteNonQueryAsync();
            return await WriteOutcomeAsync("wolverine_grants_applied", exitCode: 0);
        }
        catch
        {
            return await WriteOutcomeAsync("unavailable", exitCode: 1);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vinx.Erp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The VINX repository root was not found.");
    }

    private static async Task<int> WriteOutcomeAsync(string value, int exitCode)
    {
        await Console.Out.WriteLineAsync(value);
        return exitCode;
    }

    private static bool IsExactSubject(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 400 && value.Trim().Length == value.Length;
}
