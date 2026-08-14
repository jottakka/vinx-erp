using System.Diagnostics.Metrics;

namespace Vinx.Erp.Foundation.Observability;

public sealed class AuthorizationTelemetry
{
    public const string MeterName = "Vinx.Erp.Foundation.Authorization";
    public const string AuditUnavailableCounterName = "foundation.authorization.audit_unavailable";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> AuditUnavailableCounter =
        Meter.CreateCounter<long>(AuditUnavailableCounterName);

    internal void RecordAuditUnavailable(AuthorizationOperation operation, AuditUnavailablePhase phase)
    {
        AuditUnavailableCounter.Add(
            1,
            new KeyValuePair<string, object?>("operation", ToTagValue(operation)),
            new KeyValuePair<string, object?>("phase", ToTagValue(phase)));
    }

    private static string ToTagValue(AuthorizationOperation operation) => operation switch
    {
        AuthorizationOperation.CapabilityMap => "capability_map",
        AuthorizationOperation.MembershipMap => "membership_map",
        AuthorizationOperation.RoleAssignment => "role_assignment",
        AuthorizationOperation.MembershipRevocation => "membership_revocation",
        AuthorizationOperation.ApprovalPreparation => "approval_preparation",
        AuthorizationOperation.ApprovalConfirmation => "approval_confirmation",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported authorization operation.")
    };

    private static string ToTagValue(AuditUnavailablePhase phase) => phase switch
    {
        AuditUnavailablePhase.Authorization => "authorization",
        AuditUnavailablePhase.DenialAudit => "denial_audit",
        AuditUnavailablePhase.SuccessTransaction => "success_transaction",
        AuditUnavailablePhase.IdempotencyReplay => "idempotency_replay",
        AuditUnavailablePhase.ConcurrentReplay => "concurrent_replay",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported audit-unavailable phase.")
    };
}

internal enum AuthorizationOperation
{
    CapabilityMap,
    MembershipMap,
    RoleAssignment,
    MembershipRevocation,
    ApprovalPreparation,
    ApprovalConfirmation
}

internal enum AuditUnavailablePhase
{
    Authorization,
    DenialAudit,
    SuccessTransaction,
    IdempotencyReplay,
    ConcurrentReplay
}
