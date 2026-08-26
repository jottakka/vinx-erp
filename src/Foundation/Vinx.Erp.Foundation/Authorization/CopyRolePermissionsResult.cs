namespace Vinx.Erp.Foundation.Authorization;

public sealed record CopyRolePermissionsResult(
    Guid TargetMembershipId,
    IReadOnlyList<Guid> AppliedRoleIds);

public enum RolePermissionsCopyState
{
    Succeeded,
    Invalid,
}

/// <summary>
/// Resultado da cópia de permissões: <see cref="RolePermissionsCopyState.Invalid"/>
/// significa rejeição de contrato (400, ex.: fonte igual ao destino); nos demais
/// casos o desfecho de sucesso ou negação auditada está em <see cref="Outcome"/>.
/// </summary>
public sealed record RolePermissionsCopyResult(
    RolePermissionsCopyState State,
    AuthorizationResult<CopyRolePermissionsResult>? Outcome = null)
{
    public static RolePermissionsCopyResult Invalid() => new(RolePermissionsCopyState.Invalid);

    public static RolePermissionsCopyResult From(AuthorizationResult<CopyRolePermissionsResult> outcome) =>
        new(RolePermissionsCopyState.Succeeded, outcome);
}
