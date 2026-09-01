using Microsoft.Extensions.DependencyInjection;

namespace Vinx.Erp.Demandas;

/// <summary>
/// Raiz de composição do módulo Demandas: registra os serviços públicos do
/// módulo consumidos pelos hosts. Os hosts não devem hand-wirar registros
/// desta fatia.
/// </summary>
public static class DemandasServiceCollectionExtensions
{
    /// <summary>Registra a superfície HTTP do kanban (composer, aprovações e serviço de demandas) — ApiHost.</summary>
    public static IServiceCollection AddDemandas(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<DemandasNotificationComposer>();
        services.AddScoped<DemandasApprovalsService>();
        services.AddScoped<DemandasService>();
        return services;
    }

    /// <summary>
    /// Registra a automação agendada (composer, varredura e claim store) —
    /// WorkerHost. O tick é semeado pelo DemandasScheduler do host; a fila tem
    /// nome canônico em <see cref="DemandasQueues.Automation"/>.
    /// </summary>
    public static IServiceCollection AddDemandasAutomation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<DemandasNotificationComposer>();
        services.AddScoped<DemandasAutomationScanService>();
        services.AddScoped<DemandasSchedulerClaimStore>();
        return services;
    }
}
