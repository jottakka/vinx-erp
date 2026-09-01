namespace Vinx.Erp.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vinx.Erp.Foundation.Persistence;

/// <summary>
/// Resolução do ator inbound pela função SECURITY DEFINER
/// <c>notifications.resolve_inbound_whatsapp_actor</c>. O casamento usa
/// somente o <c>wa_id</c>: o vínculo <c>phone_number_id → empresa</c> depende
/// de política de vinculação ainda não aprovada e o identificador do canal
/// permanece no endereço para a chave de idempotência.
/// </summary>
public sealed class SqlInboundActorResolver(FoundationDbContext database)
    : IInboundActorResolver
{
    public async Task<InboundActorResolution> ResolveAsync(
        InboundChannelAddress address,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(address);

        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT resolution_state, user_id, company_id, membership_id "
            + "FROM notifications.resolve_inbound_whatsapp_actor(@wa_id)";
        var waId = command.CreateParameter();
        waId.ParameterName = "wa_id";
        waId.Value = address.WaId;
        command.Parameters.Add(waId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Inbound whatsapp actor resolution returned no row."
            );
        }

        var state = MapState(reader.GetString(0));
        return state == InboundActorResolutionState.Admitted
            ? InboundActorResolution.Admitted(
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3)
            )
            : InboundActorResolution.Closed(state);
    }

    private static InboundActorResolutionState MapState(string state) =>
        state switch
        {
            "admitted" => InboundActorResolutionState.Admitted,
            "unbound" => InboundActorResolutionState.Unbound,
            "ambiguous" => InboundActorResolutionState.Ambiguous,
            "unverified" => InboundActorResolutionState.Unverified,
            "inactive" => InboundActorResolutionState.Inactive,
            _ => throw new InvalidOperationException(
                "Inbound whatsapp actor resolution returned an unknown state."
            ),
        };
}
