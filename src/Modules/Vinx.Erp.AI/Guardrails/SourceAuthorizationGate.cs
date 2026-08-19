namespace Vinx.Erp.AI.Guardrails;

/// <summary>
/// Resultado da autorização de fontes: as liberadas, as removidas por serem
/// de outra empresa e a flag de violação cross-tenant.
/// </summary>
public sealed record SourceAuthorizationResult(
    IReadOnlyList<AgentCitedSource> AuthorizedSources,
    IReadOnlyList<AgentCitedSource> RemovedSources,
    bool CrossTenantViolation);

/// <summary>
/// Autoriza fontes citadas contra o contexto: somente fontes da empresa do
/// contexto (mesmo CompanyId) passam; fonte de outra empresa é removida e
/// marcada como violação cross-tenant.
/// </summary>
public sealed class SourceAuthorizationGate
{
    /// <summary>Separa fontes autorizadas das de outra empresa; removidas sinalizam violação.</summary>
    public SourceAuthorizationResult Authorize(
        AgentExecutionContext context,
        IReadOnlyList<AgentCitedSource> citedSources)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(citedSources);

        var authorized = new List<AgentCitedSource>();
        var removed = new List<AgentCitedSource>();
        foreach (var source in citedSources)
        {
            if (source.CompanyId == context.CompanyId)
            {
                authorized.Add(source);
            }
            else
            {
                removed.Add(source);
            }
        }

        return new SourceAuthorizationResult(authorized, removed, removed.Count > 0);
    }
}
