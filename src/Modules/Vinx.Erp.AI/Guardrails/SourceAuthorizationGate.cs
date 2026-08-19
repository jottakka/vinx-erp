namespace Vinx.Erp.AI.Guardrails;

/// <summary>
/// Autorização de fontes citadas contra o contexto: nesta fatia passa
/// somente fontes da empresa do contexto (mesmo CompanyId) e descarta as
/// demais; a próxima fatia distingue fonte cruzada com reason code próprio.
/// </summary>
public sealed class SourceAuthorizationGate
{
    /// <summary>Retorna apenas as fontes autorizadas; as de outra empresa não são expostas.</summary>
    public IReadOnlyList<AgentCitedSource> Authorize(
        AgentExecutionContext context,
        IReadOnlyList<AgentCitedSource> citedSources)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(citedSources);

        return citedSources.Where(source => source.CompanyId == context.CompanyId).ToList();
    }
}
