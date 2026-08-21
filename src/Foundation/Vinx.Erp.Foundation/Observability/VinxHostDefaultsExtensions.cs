using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        builder.Services.AddOpenTelemetry();
        builder.Services.AddHealthChecks();
        return builder;
    }
}
