using Microsoft.Extensions.DependencyInjection;

namespace Vinx.Erp.Atendimento;

/// <summary>
/// Raiz de composição do módulo Atendimento: registra os serviços públicos do
/// módulo consumidos pelos hosts. Os hosts não devem hand-wirar registros
/// desta fatia.
/// </summary>
public static class AtendimentoServiceCollectionExtensions
{
    /// <summary>Registra o serviço de cartões do atendimento (scoped).</summary>
    public static IServiceCollection AddAtendimento(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<AtendimentoCardService>();
        return services;
    }
}
