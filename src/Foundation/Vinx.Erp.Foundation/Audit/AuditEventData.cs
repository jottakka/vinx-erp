using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Audit;

public sealed record AuditEventData(
    Guid ActorUserId,
    Guid? RequestedCompanyId,
    AuditSource Source,
    string Action,
    string ResourceType,
    Guid? ResourceId,
    AuditOutcome Outcome,
    Guid CorrelationId,
    string? Reason,
    string MetadataJson);
