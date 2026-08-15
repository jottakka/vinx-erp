using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Audit;

public sealed class AuditAppender(FoundationDbContext database)
{
    public Task AppendAsync(AuditEventData eventData, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT audit.append_event(
                {eventData.ActorUserId},
                {ToDatabaseValue(eventData.Scope)},
                {eventData.RequestedCompanyId},
                {ToDatabaseValue(eventData.Source)},
                {eventData.Action},
                {eventData.ResourceType},
                {eventData.ResourceId},
                {ToDatabaseValue(eventData.Outcome)},
                {eventData.CorrelationId},
                {eventData.Reason},
                {eventData.MetadataJson}::jsonb)
            """,
            cancellationToken);

    private static string ToDatabaseValue(AuditScope scope) => scope switch
    {
        AuditScope.Global => "global",
        AuditScope.Company => "company",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported audit scope.")
    };

    private static string ToDatabaseValue(AuditSource source) => source switch
    {
        AuditSource.Web => "web",
        AuditSource.Mcp => "mcp",
        AuditSource.Worker => "worker",
        AuditSource.Ai => "ai",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported audit source.")
    };

    private static string ToDatabaseValue(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Failure => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported audit outcome.")
    };
}
