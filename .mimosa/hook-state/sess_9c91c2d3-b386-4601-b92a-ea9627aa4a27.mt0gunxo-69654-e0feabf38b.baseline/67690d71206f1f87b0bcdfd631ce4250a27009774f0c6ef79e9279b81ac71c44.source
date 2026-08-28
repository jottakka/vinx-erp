using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Audit;

public sealed record AuditEventData
{
    public AuditEventData(
        Guid actorUserId,
        AuditScope scope,
        Guid? requestedCompanyId,
        AuditSource source,
        string action,
        string resourceType,
        Guid? resourceId,
        AuditOutcome outcome,
        Guid correlationId,
        string? reason,
        string metadataJson)
    {
        switch (scope)
        {
            case AuditScope.Global when requestedCompanyId is not null:
                throw new ArgumentException("Global audit events cannot carry a company.", nameof(requestedCompanyId));
            case AuditScope.Company when requestedCompanyId is null:
                throw new ArgumentException("Company audit events require a company.", nameof(requestedCompanyId));
            case AuditScope.Global:
            case AuditScope.Company:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported audit scope.");
        }

        ActorUserId = actorUserId;
        Scope = scope;
        RequestedCompanyId = requestedCompanyId;
        Source = source;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Outcome = outcome;
        CorrelationId = correlationId;
        Reason = reason;
        MetadataJson = metadataJson;
    }

    public Guid ActorUserId { get; }
    public AuditScope Scope { get; }
    public Guid? RequestedCompanyId { get; }
    public AuditSource Source { get; }
    public string Action { get; }
    public string ResourceType { get; }
    public Guid? ResourceId { get; }
    public AuditOutcome Outcome { get; }
    public Guid CorrelationId { get; }
    public string? Reason { get; }
    public string MetadataJson { get; }
}
