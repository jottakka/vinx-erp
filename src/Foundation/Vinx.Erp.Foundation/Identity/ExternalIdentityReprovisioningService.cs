using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Platform;

namespace Vinx.Erp.Foundation.Identity;

public sealed record ExternalIdentityReprovisioningCommand(
    Guid UserId,
    Guid ExpectedExternalIdentityId,
    string ExpectedOldSubject,
    string NewSubject,
    Guid CorrelationId
);

public enum ExternalIdentityReprovisioningState
{
    Reprovisioned,
    AlreadyConfigured,
    ProviderNotDeleted,
    ProviderRejected,
    EmailMismatch,
    TupleMismatch,
    Unavailable,
}

/// <summary>Reativa a mesma pessoa somente após a baixa comprovada da identidade antiga no provedor.</summary>
public sealed class ExternalIdentityReprovisioningService(
    FoundationDbContext database,
    SupabaseAdminUserVerifier verifier,
    SupabaseAuthOptions authentication,
    ILogger<ExternalIdentityReprovisioningService> logger
)
{
    private const string Operation = "external_identity_reprovisioning";
    private const string Module = "foundation";
    private const string EventTemplate =
        "External identity reprovisioning {Operation} {Module} {Result} {Reason}";

    public async Task<ExternalIdentityReprovisioningState> ReprovisionAsync(
        ExternalIdentityReprovisioningCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!IsValid(command))
        {
            return ExternalIdentityReprovisioningState.TupleMismatch;
        }

        var oldVerification = await verifier.VerifyAsync(
            command.ExpectedOldSubject,
            cancellationToken
        );
        if (oldVerification.Category is not PlatformOperatorVerificationCategory.NotFound)
        {
            return oldVerification.Category is PlatformOperatorVerificationCategory.ProviderUnavailable
                ? ExternalIdentityReprovisioningState.Unavailable
                : oldVerification.Category is PlatformOperatorVerificationCategory.Verified
                    ? ExternalIdentityReprovisioningState.ProviderNotDeleted
                    : ExternalIdentityReprovisioningState.ProviderRejected;
        }

        var newVerification = await verifier.VerifyAsync(command.NewSubject, cancellationToken);
        if (!newVerification.IsVerified || !newVerification.TryGetVerifiedEmail(out var providerEmail))
        {
            return newVerification.Category is PlatformOperatorVerificationCategory.ProviderUnavailable
                ? ExternalIdentityReprovisioningState.Unavailable
                : ExternalIdentityReprovisioningState.ProviderRejected;
        }

        var retainedEmail = await database
            .Users.Where(user => user.Id == command.UserId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);
        if (!SameEmail(retainedEmail, providerEmail))
        {
            return ExternalIdentityReprovisioningState.EmailMismatch;
        }

        try
        {
            var status = await database
                .Database.SqlQuery<string>(
                    $"""
                    SELECT status AS "Value"
                    FROM foundation.reprovision_external_identity(
                        {command.UserId},
                        {command.ExpectedExternalIdentityId},
                        {authentication.NormalizedIssuer},
                        {command.ExpectedOldSubject},
                        {command.NewSubject},
                        {command.CorrelationId})
                    """
                )
                .SingleOrDefaultAsync(cancellationToken);
            return status switch
            {
                "reprovisioned" => ExternalIdentityReprovisioningState.Reprovisioned,
                "already_configured" => ExternalIdentityReprovisioningState.AlreadyConfigured,
                "conflict" or "invalid" => ExternalIdentityReprovisioningState.TupleMismatch,
                _ => LogUnavailable(),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return LogUnavailable(exception);
        }
    }

    private static bool IsValid(ExternalIdentityReprovisioningCommand command) =>
        command.UserId != Guid.Empty
        && command.ExpectedExternalIdentityId != Guid.Empty
        && command.CorrelationId != Guid.Empty
        && IsValidSubject(command.ExpectedOldSubject)
        && IsValidSubject(command.NewSubject);

    private static bool IsValidSubject(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 400 && value.Trim().Length == value.Length;

    private static bool SameEmail(string? retainedEmail, string providerEmail) =>
        retainedEmail is not null
        && string.Equals(
            retainedEmail.Trim(),
            providerEmail.Trim(),
            StringComparison.OrdinalIgnoreCase
        );

    private ExternalIdentityReprovisioningState LogUnavailable(Exception? exception = null)
    {
        logger.LogError(
            exception,
            EventTemplate,
            Operation,
            Module,
            "unavailable",
            "reprovisioning_execution_failed"
        );
        return ExternalIdentityReprovisioningState.Unavailable;
    }
}
