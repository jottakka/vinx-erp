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
    public static TBuilder AddVinxHostDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddVinxLogging();
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                // Instrumentação padrão do runtime .NET e do ASP.NET Core.
                .AddRuntimeInstrumentation()
                .AddAspNetCoreInstrumentation()
                // Fontes nativas do ASP.NET Core e do Npgsql, disponíveis sem
                // pacotes extras. Medidores de módulos são adicionados por cada
                // host (a Foundation não referencia módulos).
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("Npgsql"));

        // Exportação OTLP somente quando o endpoint está configurado; sem a
        // variável, nenhum exportador é registrado e o dev local fica intacto.
        // A sobrecarga sem argumentos lê OTEL_EXPORTER_OTLP_* direto do
        // ambiente (endpoint, protocolo, headers).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        builder.Services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["postgres"]);
        return builder;
    }
}
