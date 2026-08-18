using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Platform;

/// <summary>
/// Executa a transição mínima do convite reservada ao worker de entrega.
/// </summary>
public sealed class OwnerInvitationDeliveryIngressService(FoundationDbContext database)
{
    public async Task<OwnerInvitationDeliveryIngressResult> AcceptAsync(
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        if (invitationId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            var connection = database.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "SELECT status, provider_pending_at, transitioned FROM foundation.accept_owner_invitation_delivery(@invitation_id, @correlation_id)";
            AddParameter(command, "invitation_id", invitationId);
            AddParameter(command, "correlation_id", correlationId);

            OwnerInvitationDeliveryIngressResult result;
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

                result = new OwnerInvitationDeliveryIngressResult(
                    status,
                    reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetBoolean(2)
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

    private static void AddParameter(IDbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed record OwnerInvitationDeliveryIngressResult(
    string Status,
    DateTimeOffset? ProviderPendingAt,
    bool Transitioned
);

/// <summary>
/// Marca entradas inválidas ou inconsistentes para descarte durável sem expor dados.
/// </summary>
public sealed class OwnerInvitationIngressPermanentException : Exception;
