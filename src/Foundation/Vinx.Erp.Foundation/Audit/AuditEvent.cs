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
        if (requestedCompanyId is null
            && (!string.Equals(action, "company_context_required", StringComparison.Ordinal)
                || outcome != AuditOutcome.Denied))
        {
            throw new ArgumentException(
                "A requested company is required unless the denial is company_context_required.",
                nameof(requestedCompanyId));
        }

        Id = id;
        ActorUserId = actorUserId;
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
