using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Vinx.Erp.Foundation.Observability;

/// <summary>
/// Configuração mínima compartilhada pelos hosts (API, worker e MCP):
/// logging estruturado JSON, telemetria OpenTelemetry e health checks.
/// Remove o setup repetido — e divergente — entre os Program.cs dos hosts.
/// </summary>
public static class VinxHostDefaultsExtensions
{
    /// <param name="registerPostgresHealthCheck">
    /// Registra o probe real de Postgres somente em hosts que dependem do
    /// banco (ApiHost/WorkerHost). O McpHost não registra Npgsql nem
    /// FoundationDbContext: com o check ele teria readiness permanentemente
    /// 503.
    /// </param>
    public static TBuilder AddVinxHostDefaults<TBuilder>(
        this TBuilder builder,
        bool registerPostgresHealthCheck = true)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddVinxLogging();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                // Fontes nativas do ASP.NET Core e do Npgsql, disponíveis sem
                // pacotes extras. Medidores de módulos são adicionados por cada
                // host (a Foundation não referencia módulos).
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("Npgsql"));
        var healthChecks = builder.Services.AddHealthChecks();
        if (registerPostgresHealthCheck)
        {
            healthChecks.AddCheck<PostgresHealthCheck>("postgres", tags: ["postgres"]);
        }
        return builder;
    }
}
