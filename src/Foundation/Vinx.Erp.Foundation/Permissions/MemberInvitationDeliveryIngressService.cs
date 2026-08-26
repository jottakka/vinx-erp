using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Permissions;

/// <summary>
/// Executa a transição mínima do convite de membro reservada ao worker de entrega.
/// </summary>
public sealed class MemberInvitationDeliveryIngressService(FoundationDbContext database)
{
    public async Task<MemberInvitationDeliveryIngressResult> AcceptAsync(
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        if (invitationId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new MemberInvitationIngressPermanentException();
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            var result = await database.AcceptMemberInvitationDeliveryAsync(
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
            throw new MemberInvitationIngressPermanentException();
        }
    }
}

public sealed record MemberInvitationDeliveryIngressResult(
    string Status,
    DateTimeOffset? ProviderPendingAt,
    bool Transitioned
);

/// <summary>
/// Marca entradas inválidas ou inconsistentes para descarte durável sem expor dados.
/// </summary>
public sealed class MemberInvitationIngressPermanentException : Exception;
