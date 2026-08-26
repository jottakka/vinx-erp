using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Paguai;

/// <summary>
/// Resolve, no momento da entrega (dispatch time), as memberships ativas que
/// detêm <c>paguai.draft.approve</c> na empresa. Nunca snapshotado: a lista
/// é consultada pelo worker quando o evento chega à fila.
/// </summary>
public sealed class PaguaiApproverDirectory(FoundationDbContext database)
{
    /// <summary>Usuários distintos com a capability de aprovação, excluindo quem criou o rascunho.</summary>
    public async Task<IReadOnlyList<Guid>> ResolveApproverUserIdsAsync(
        Guid companyId,
        Guid? excludeUserId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty)
        {
            return [];
        }

        var connection = database.Database.GetDbConnection();
        var ownsOpen = false;
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
                ownsOpen = true;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT user_id FROM paguai.list_draft_approver_user_ids(@company_id, @exclude_user_id)";
            AddParameter(command, "company_id", companyId);
            AddParameter(command, "exclude_user_id", excludeUserId);

            var recipients = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    recipients.Add(reader.GetGuid(0));
                }
            }

            return recipients;
        }
        finally
        {
            if (ownsOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
