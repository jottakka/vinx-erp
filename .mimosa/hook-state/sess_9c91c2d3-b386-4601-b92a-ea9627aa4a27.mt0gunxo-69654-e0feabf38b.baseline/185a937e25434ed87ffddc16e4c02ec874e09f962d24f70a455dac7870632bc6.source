using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Identity;

public sealed class SelfSessionRevocationService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    ILogger<SelfSessionRevocationService> logger)
{
    private const string Operation = "session_revocation";
    private const string Module = "identity";
    private const string RevokedAction = "identity_sessions_revoked";
    private const string RevocationMetadata = "{{\"revoked_session_count\":{0}}}";
    private const string UnavailableReason = "database_or_audit_unavailable";
    private const string EventTemplate = "Session revocation {Operation} {Module} {Result} {Reason}";

    public async Task<SelfSessionRevocationResult> RevokeAllAsync(
        ActorContext actor,
        AuditSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var revokedSessionCount = await database.RevokeAllUserSessionsAsync(actor.UserId, cancellationToken);
            if (revokedSessionCount == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return SelfSessionRevocationResult.NoChange();
            }

            await auditAppender.AppendAsync(new AuditEventData(
                actor.UserId,
                AuditScope.Global,
                null,
                source,
                RevokedAction,
                "user",
                actor.UserId,
                AuditOutcome.Success,
                actor.CorrelationId,
                null,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    RevocationMetadata,
                    revokedSessionCount)), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SelfSessionRevocationResult.Revoked();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(
                exception,
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                UnavailableReason);
            return SelfSessionRevocationResult.Unavailable();
        }
    }
}
