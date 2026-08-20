using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Identity;

public sealed class InternalSessionAdmissionService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    ILogger<InternalSessionAdmissionService> logger)
{
    private const string Operation = "session_admission";
    private const string Module = "identity";
    private const string AdmittedAction = "identity_session_admitted";
    private const string DeniedAction = "identity_session_admission_denied";
    private const string AuthenticationNotAfterBarrier = "authentication_not_after_barrier";
    private const string SessionRevoked = "session_revoked";
    private const string SessionIdentityMismatch = "session_identity_mismatch";
    private const string UnavailableReason = "database_or_audit_unavailable";
    private const string EventTemplate = "Session admission {Operation} {Module} {Result} {Reason}";

    public async Task<SessionAdmissionResult> AdmitAsync(
        Guid userId,
        ExternalIdentityProof proof,
        AuditSource source,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId
        });

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var lockedUser = await LockUserAsync(userId, cancellationToken);
            if (!lockedUser.Exists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return LogUnavailable(new InvalidOperationException("The locked internal user does not exist."));
            }

            if (lockedUser.SessionsValidAfter is { } sessionsValidAfter
                && proof.ProviderAuthenticatedAt <= sessionsValidAfter)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditDeniedAsync(
                    userId,
                    source,
                    correlationId,
                    AuthenticationNotAfterBarrier,
                    LogLevel.Debug,
                    cancellationToken);
            }

            var existing = await FindSessionAsync(proof, cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ExistingSessionResultAsync(existing, userId, source, correlationId, cancellationToken);
            }

            var session = new UserSession(
                Guid.NewGuid(),
                userId,
                proof.Key.Issuer,
                proof.ProviderSessionId,
                proof.ProviderAuthenticatedAt,
                proof.TokenExpiresAt);
            database.UserSessions.Add(session);
            await auditAppender.AppendAsync(new AuditEventData(
                userId,
                AuditScope.Global,
                null,
                source,
                AdmittedAction,
                "user_session",
                session.Id,
                AuditOutcome.Success,
                correlationId,
                null,
                "{}"), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
            logger.LogInformation(EventTemplate, Operation, Module, "admitted", "new_session");
#pragma warning restore CA1873
            return SessionAdmissionResult.Admitted();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            database.ChangeTracker.Clear();
            return await ResolveUniqueRaceAsync(userId, proof, source, correlationId, exception, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            return LogUnavailable(exception);
        }
    }

    private async Task<SessionAdmissionResult> ResolveUniqueRaceAsync(
        Guid userId,
        ExternalIdentityProof proof,
        AuditSource source,
        Guid correlationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var lockedUser = await LockUserAsync(userId, cancellationToken);
            if (!lockedUser.Exists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return LogUnavailable(new InvalidOperationException("The locked internal user does not exist."));
            }

            if (lockedUser.SessionsValidAfter is { } sessionsValidAfter
                && proof.ProviderAuthenticatedAt <= sessionsValidAfter)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditDeniedAsync(
                    userId,
                    source,
                    correlationId,
                    AuthenticationNotAfterBarrier,
                    LogLevel.Debug,
                    cancellationToken);
            }

            var existing = await FindSessionAsync(proof, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            if (existing is null)
            {
                return LogUnavailable(exception);
            }

            return await ExistingSessionResultAsync(existing, userId, source, correlationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception retryException)
        {
            database.ChangeTracker.Clear();
            return LogUnavailable(retryException);
        }
    }

    private async Task<SessionAdmissionResult> ExistingSessionResultAsync(
        UserSession session,
        Guid userId,
        AuditSource source,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (session.UserId != userId)
        {
            return await AuditDeniedAsync(
                userId,
                source,
                correlationId,
                SessionIdentityMismatch,
                LogLevel.Warning,
                cancellationToken);
        }

        if (session.RevokedAt is not null)
        {
            return await AuditDeniedAsync(
                userId,
                source,
                correlationId,
                SessionRevoked,
                LogLevel.Debug,
                cancellationToken);
        }

        return SessionAdmissionResult.Active();
    }

    private async Task<SessionAdmissionResult> AuditDeniedAsync(
        Guid userId,
        AuditSource source,
        Guid correlationId,
        string reason,
        LogLevel logLevel,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await auditAppender.AppendAsync(new AuditEventData(
                userId,
                AuditScope.Global,
                null,
                source,
                DeniedAction,
                "user_session",
                null,
                AuditOutcome.Denied,
                correlationId,
                reason,
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
            logger.Log(logLevel, EventTemplate, Operation, Module, "denied", reason);
#pragma warning restore CA1873
            return SessionAdmissionResult.Denied(reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            return LogUnavailable(exception);
        }
    }

    private async Task<UserSession?> FindSessionAsync(
        ExternalIdentityProof proof,
        CancellationToken cancellationToken) =>
        await database.UserSessions.SingleOrDefaultAsync(session =>
            session.Issuer == proof.Key.Issuer
            && session.ProviderSessionId == proof.ProviderSessionId,
            cancellationToken);

    private async Task<LockedUser> LockUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await database.LockUserForSessionAdmissionAsync(userId, cancellationToken);

    private SessionAdmissionResult LogUnavailable(Exception? exception = null)
    {
        if (exception is null)
        {
            logger.LogError(EventTemplate, Operation, Module, "unavailable", UnavailableReason);
        }
        else
        {
            logger.LogError(
                new SessionAdmissionUnavailableException(),
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                UnavailableReason);
        }

        return SessionAdmissionResult.Unavailable();
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed class SessionAdmissionUnavailableException : Exception
    {
        public SessionAdmissionUnavailableException()
            : base("Internal session admission is unavailable.")
        {
        }
    }
}
