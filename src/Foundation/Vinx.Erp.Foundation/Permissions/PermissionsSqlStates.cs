namespace Vinx.Erp.Foundation.Permissions;

/// <summary>
/// SQLSTATEs de aplicação das funções de permissão. O código — e não a
/// mensagem de erro do Postgres — é o contrato de classificação usado pelo
/// C#; a mensagem é texto para humanos.
/// </summary>
public static class PermissionsSqlStates
{
    /// <summary>
    /// Gate <c>foundation.roles.assign</c> negado (members, roles e invite).
    /// Difere de <c>42501</c> deliberadamente: mantém a negação de capability
    /// distinguível de outras falhas de privilégio (grants ausentes, RLS) que
    /// usam o mesmo SQLSTATE e devem continuar respondendo como indisponíveis
    /// em vez de 403.
    /// </summary>
    public const string CapabilityDenied = "PX425";
}
