using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>
/// Entrada da política de contexto: ator autenticado, perfil aprovado no
/// servidor e empresa solicitada (nula quando o journey exige esclarecimento).
/// </summary>
public sealed record AgentContextRequest(
    ActorContext Actor,
    AgentTaskProfile Profile,
    Guid? RequestedCompanyId);

/// <summary>
/// Política pura de resolução de contexto do agente: decide empresa e
/// capability sobre o snapshot de autorização pronto, ANTES de qualquer
/// chamada de modelo, sempre fail-closed. A consulta (com RLS e auditoria)
/// pertence à Foundation; o snapshot recebido já limita o universo — nenhuma
/// empresa fora dele é consultada ou exposta, e o policy não mantém cache.
/// </summary>
public static class AgentPolicy
{
    private const string MessageEmptyReason = "message_empty";
    private const string ProfileUnknownReason = "profile_unknown";
    private const string CompanyRequiredReason = "company_required";
    private const string CompanyNotAuthorizedReason = "company_not_authorized";
    private const string CapabilityDeniedReason = "capability_denied";
    private const string ResolvedReason = "resolved";

    /// <summary>
    /// Avalia o contexto na ordem fixa da spec (seção 4): esclarecimento,
    /// negação de empresa, negação de capability e só então sucesso.
    /// </summary>
    public static AgentContextResolution EvaluateContext(
        ActorAuthorizationSnapshot snapshot,
        AgentContextRequest request)
    {
        if (request.RequestedCompanyId is null)
        {
            return new AgentContextResolution(
                AgentContextResolutionStatus.NeedsClarification,
                CompanyRequiredReason);
        }

        var membership = snapshot
            .Companies.FirstOrDefault(company => company.CompanyId == request.RequestedCompanyId);
        if (membership is null)
        {
            return new AgentContextResolution(
                AgentContextResolutionStatus.Denied,
                CompanyNotAuthorizedReason);
        }

        var hasRequiredCapability = membership.Capabilities.Any(capability =>
            capability.Name == request.Profile.RequiredCapabilityName
            && capability.Scopes.Contains(CapabilityScope.Company)
        );
        if (!hasRequiredCapability)
        {
            return new AgentContextResolution(
                AgentContextResolutionStatus.Denied,
                CapabilityDeniedReason);
        }

        return new AgentContextResolution(
            AgentContextResolutionStatus.Resolved,
            ResolvedReason,
            new AgentExecutionContext(
                request.Actor.UserId,
                request.Actor.CorrelationId,
                membership.CompanyId,
                membership.CompanyName,
                membership.MembershipId,
                request.Profile.ProfileId,
                membership.Capabilities.Select(capability => capability.Name).ToHashSet()));
    }

    /// <summary>
    /// Valida a entrada de API antes da resolução de contexto; null = válida,
    /// senão reason code estável.
    /// </summary>
    public static string? ValidateRequest(string message, AgentTaskProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return MessageEmptyReason;
        }

        if (profile is null)
        {
            return ProfileUnknownReason;
        }

        return null;
    }
}
