using Microsoft.Extensions.DependencyInjection;

namespace Vinx.Erp.Paguai;

/// <summary>
/// Raiz de composição do módulo Paguai: registra os serviços públicos do
/// módulo consumidos pelos hosts. Os hosts não devem hand-wirar registros
/// desta fatia.
/// </summary>
public static class PaguaiServiceCollectionExtensions
{
    /// <summary>Registra o fluxo completo de rascunhos e propostas — ApiHost.</summary>
    public static IServiceCollection AddPaguai(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PaguaiDraftService>();
        services.AddScoped<PaguaiProposalService>();
        services.AddScoped<PaguaiProposalConfirmationService>();
        return services;
    }

    /// <summary>Registra somente o serviço de propostas consumido pelo worker — WorkerHost.</summary>
    public static IServiceCollection AddPaguaiProposal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PaguaiProposalService>();
        return services;
    }
}
