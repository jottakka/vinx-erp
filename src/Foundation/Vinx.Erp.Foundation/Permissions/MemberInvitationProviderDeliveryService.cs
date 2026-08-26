using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Platform;

namespace Vinx.Erp.Foundation.Permissions;

/// <summary>
/// Coordena a chamada única ao provedor fora de transações do banco e registra
/// somente seu resultado seguro na reserva do convite de membro.
/// </summary>
public sealed class MemberInvitationProviderDeliveryService(
    FoundationDbContext database,
    MemberInvitationDeliveryIngressService ingress,
    IHttpClientFactory httpClientFactory,
    SupabaseAuthOptions authentication,
    SupabaseAdminOptions administration
)
{
    public async Task<MemberInvitationProviderDeliveryResult> DeliverAsync(
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        if (invitationId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new MemberInvitationIngressPermanentException();
        }

        var ingressResult = await ingress.AcceptAsync(
            invitationId,
            correlationId,
            cancellationToken
        );
        if (IsFinal(ingressResult.Status))
        {
            return new MemberInvitationProviderDeliveryResult(ingressResult.Status, false);
        }

        if (
            !string.Equals(ingressResult.Status, "pending_provider", StringComparison.Ordinal)
            && !string.Equals(ingressResult.Status, "provider_in_flight", StringComparison.Ordinal)
        )
        {
            throw new MemberInvitationIngressPermanentException();
        }

        var claim = await ClaimAsync(invitationId, correlationId, cancellationToken);
        if (IsFinal(claim.Status))
        {
            return new MemberInvitationProviderDeliveryResult(claim.Status, false);
        }

        if (
            !string.Equals(claim.Status, "provider_in_flight", StringComparison.Ordinal)
            || claim.AttemptId is null
        )
        {
            throw new MemberInvitationIngressPermanentException();
        }

        if (!claim.Claimed)
        {
            return new MemberInvitationProviderDeliveryResult(claim.Status, false);
        }

        if (string.IsNullOrWhiteSpace(claim.MemberEmail))
        {
            throw new MemberInvitationIngressPermanentException();
        }

        var provider = new SupabaseAdminInvitationClient(
            httpClientFactory.CreateClient(SupabaseAdminInvitationClient.HttpClientName),
            authentication,
            administration
        );
        var providerResult = await provider.InviteAsync(claim.MemberEmail, cancellationToken);
        var finalization = providerResult.Kind switch
        {
            SupabaseInvitationResultKind.Succeeded
                when providerResult.Subject is Guid subject => (
                "success",
                authentication.NormalizedIssuer,
                (Guid?)subject,
                (string?)null
            ),
            SupabaseInvitationResultKind.Rejected => (
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
        return new MemberInvitationProviderDeliveryResult(finalized.Status, finalized.Finalized);
    }

    private async Task<MemberInvitationProviderClaim> ClaimAsync(
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
            var claim = await database.ClaimMemberInvitationProviderDeliveryAsync(
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
            throw new MemberInvitationIngressPermanentException();
        }
    }

    private async Task<MemberInvitationProviderFinalization> FinalizeAsync(
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
            var finalized = await database.FinalizeMemberInvitationProviderDeliveryAsync(
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
            throw new MemberInvitationIngressPermanentException();
        }
    }

    private static bool IsFinal(string status) =>
        status is "pending_acceptance" or "provider_failed" or "provider_ambiguous";
}

public sealed record MemberInvitationProviderDeliveryResult(string Status, bool Finalized);
