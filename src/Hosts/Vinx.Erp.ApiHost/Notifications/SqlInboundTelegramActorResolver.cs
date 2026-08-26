using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Resolve o ator inbound pela função SECURITY DEFINER
/// <c>notifications.resolve_inbound_telegram_actor</c>. Somente o chat
/// privado validado participa da resolução; a função nunca escolhe tenant.
/// </summary>
public sealed class SqlInboundTelegramActorResolver(FoundationDbContext database)
    : IInboundTelegramActorResolver
{
    public async Task<InboundActorResolution> ResolveAsync(
        long chatId,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT resolution_state, user_id, company_id, membership_id "
            + "FROM notifications.resolve_inbound_telegram_actor(@chat_id)";
        var chatIdParameter = command.CreateParameter();
        chatIdParameter.ParameterName = "chat_id";
        chatIdParameter.Value = chatId;
        command.Parameters.Add(chatIdParameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Inbound telegram actor resolution returned no row."
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
                "Inbound telegram actor resolution returned an unknown state."
            ),
        };
}
