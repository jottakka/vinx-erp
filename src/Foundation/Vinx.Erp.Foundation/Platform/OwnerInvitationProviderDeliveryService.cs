using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Platform;

/// <summary>
/// Coordena a chamada única ao provedor fora de transações do banco e registra
/// somente seu resultado seguro na reserva de convite.
/// </summary>
public sealed class OwnerInvitationProviderDeliveryService(
    FoundationDbContext database,
    OwnerInvitationDeliveryIngressService ingress,
    IHttpClientFactory httpClientFactory,
    SupabaseAuthOptions authentication,
    SupabaseAdminOptions administration
)
{
    public async Task<OwnerInvitationProviderDeliveryResult> DeliverAsync(
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        if (invitationId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        var ingressResult = await ingress.AcceptAsync(
            invitationId,
            correlationId,
            cancellationToken
        );
        if (IsFinal(ingressResult.Status))
        {
            return new OwnerInvitationProviderDeliveryResult(ingressResult.Status, false);
        }

        if (
            !string.Equals(ingressResult.Status, "pending_provider", StringComparison.Ordinal)
            && !string.Equals(ingressResult.Status, "provider_in_flight", StringComparison.Ordinal)
        )
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        var claim = await ClaimAsync(invitationId, correlationId, cancellationToken);
        if (IsFinal(claim.Status))
        {
            return new OwnerInvitationProviderDeliveryResult(claim.Status, false);
        }

        if (
            !string.Equals(claim.Status, "provider_in_flight", StringComparison.Ordinal)
            || claim.AttemptId is null
        )
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        if (!claim.Claimed)
        {
            return new OwnerInvitationProviderDeliveryResult(claim.Status, false);
        }

        if (string.IsNullOrWhiteSpace(claim.OwnerEmail))
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        var provider = new SupabaseAdminOwnerInvitationClient(
            httpClientFactory.CreateClient(SupabaseAdminOwnerInvitationClient.HttpClientName),
            authentication,
            administration
        );
        var providerResult = await provider.InviteAsync(claim.OwnerEmail, cancellationToken);
        var finalization = providerResult.Kind switch
        {
            SupabaseOwnerInvitationResultKind.Succeeded
                when providerResult.Subject is Guid subject => (
                "success",
                authentication.NormalizedIssuer,
                (Guid?)subject,
                (string?)null
            ),
            SupabaseOwnerInvitationResultKind.Rejected => (
                "failed",
                (string?)null,
                (Guid?)null,
                "provider_rejected"
            ),
            _ => ("ambiguous", (string?)null, (Guid?)null, "provider_ambiguous"),
        };
        var finalized = await FinalizeAsync(
            invitationId,
            claim.AttemptId.Value,
            correlationId,
            finalization.Item1,
            finalization.Item2,
            finalization.Item3,
            finalization.Item4,
            cancellationToken
        );
        return new OwnerInvitationProviderDeliveryResult(finalized.Status, finalized.Finalized);
    }

    private async Task<OwnerInvitationProviderClaim> ClaimAsync(
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        try
        {
            var claim = await database.ClaimOwnerInvitationProviderDeliveryAsync(
                invitationId,
                correlationId,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return claim;
        }
        catch (PostgresException exception)
            when (exception.SqlState
                    is PostgresErrorCodes.CheckViolation
                        or PostgresErrorCodes.InvalidParameterValue
            )
        {
            throw new OwnerInvitationIngressPermanentException();
        }
    }

    private async Task<OwnerInvitationProviderFinalization> FinalizeAsync(
        Guid invitationId,
        Guid attemptId,
        Guid correlationId,
        string outcome,
        string? issuer,
        Guid? subject,
        string? failureReason,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        try
        {
            var finalized = await database.FinalizeOwnerInvitationProviderDeliveryAsync(
                invitationId,
                attemptId,
                correlationId,
                outcome,
                issuer,
                subject,
                failureReason,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return finalized;
        }
        catch (PostgresException exception)
            when (exception.SqlState
                    is PostgresErrorCodes.CheckViolation
                        or PostgresErrorCodes.InvalidParameterValue
            )
        {
            throw new OwnerInvitationIngressPermanentException();
        }
    }

    private static bool IsFinal(string status) =>
        status is "pending_acceptance" or "provider_failed" or "provider_ambiguous";
}

public sealed record OwnerInvitationProviderDeliveryResult(string Status, bool Finalized);
