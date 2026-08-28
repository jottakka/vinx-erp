using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Audit;

public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public AuditEvent(
        Guid id,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataJson);
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

        Id = id;
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
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid ActorUserId { get; private set; }
    public AuditScope Scope { get; private set; }
    public Guid? RequestedCompanyId { get; private set; }
    public AuditSource Source { get; private set; }
    public string Action { get; private set; } = null!;
    public string ResourceType { get; private set; } = null!;
    public Guid? ResourceId { get; private set; }
    public AuditOutcome Outcome { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string? Reason { get; private set; }
    public string MetadataJson { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
}
