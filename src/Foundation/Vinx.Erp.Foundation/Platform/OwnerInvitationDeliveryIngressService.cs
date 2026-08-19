using Microsoft.EntityFrameworkCore;
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
            var result = await database.AcceptOwnerInvitationDeliveryAsync(
                invitationId,
                correlationId,
                cancellationToken
            );
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
