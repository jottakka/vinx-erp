using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    private async Task<ProviderClaim> ClaimAsync(
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT status, attempt_id, owner_email, claimed FROM foundation.claim_owner_invitation_provider_delivery(@invitation_id, @correlation_id)";
        AddParameter(command, "invitation_id", invitationId);
        AddParameter(command, "correlation_id", correlationId);

        ProviderClaim result;
        try
        {
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new OwnerInvitationIngressPermanentException();
                }

                var status = reader.GetString(0);
                if (string.Equals(status, "not_found", StringComparison.Ordinal))
                {
                    throw new OwnerInvitationIngressPermanentException();
                }

                result = new ProviderClaim(
                    status,
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetBoolean(3)
                );
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
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

    private async Task<ProviderFinalization> FinalizeAsync(
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
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT status, finalized FROM foundation.finalize_owner_invitation_provider_delivery(@invitation_id, @attempt_id, @correlation_id, @outcome, @issuer, @subject, @failure_reason)";
        AddParameter(command, "invitation_id", invitationId);
        AddParameter(command, "attempt_id", attemptId);
        AddParameter(command, "correlation_id", correlationId);
        AddParameter(command, "outcome", outcome);
        AddParameter(command, "issuer", issuer);
        AddParameter(command, "subject", subject);
        AddParameter(command, "failure_reason", failureReason);

        ProviderFinalization result;
        try
        {
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new OwnerInvitationIngressPermanentException();
                }

                var status = reader.GetString(0);
                if (string.Equals(status, "not_found", StringComparison.Ordinal))
                {
                    throw new OwnerInvitationIngressPermanentException();
                }

                result = new ProviderFinalization(status, reader.GetBoolean(1));
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
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

    private static void AddParameter(IDbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddParameter(IDbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is null ? DBNull.Value : value;
        command.Parameters.Add(parameter);
    }

    private static void AddParameter(IDbCommand command, string name, Guid? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is null ? DBNull.Value : value.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record ProviderClaim(
        string Status,
        Guid? AttemptId,
        string? OwnerEmail,
        bool Claimed
    );

    private sealed record ProviderFinalization(string Status, bool Finalized);
}

public sealed record OwnerInvitationProviderDeliveryResult(string Status, bool Finalized);
