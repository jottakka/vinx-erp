using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.OpenApi;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.ApiHost.Notifications;

public sealed record SelectAgentChannelCompanyRequest(
    Guid InboundMessageId,
    Guid CompanyId,
    Guid RequestId);

/// <summary>
/// Seleção autenticada e revogável de empresa para uma identidade Telegram já
/// vinculada. A operação nunca publica run nem processa a mensagem anterior.
/// </summary>
public static class AgentChannelCompanySelectionEndpoints
{
    public static IEndpointRouteBuilder MapAgentChannelCompanySelectionEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/agent/channel-company-selection",
                SelectAsync)
            .RequireAuthorization()
            .RequireRateLimiting(ApiConstants.ProtectedRateLimitPolicy)
            .WithMetadata(new JsonRequestBodyContract(typeof(SelectAgentChannelCompanyRequest)))
            .WithMetadata(new BrowserBffAntiforgeryRequiredMetadata())
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<IResult> SelectAsync(
        HttpContext context,
        SelectAgentChannelCompanyRequest request,
        FoundationDbContext database,
        CancellationToken cancellationToken)
    {
        if (request.InboundMessageId == Guid.Empty
            || request.CompanyId == Guid.Empty
            || request.RequestId == Guid.Empty)
        {
            return Results.Json(
                new { code = "invalid_request" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = context.GetRequiredActorContext();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken).ConfigureAwait(false);
            await FoundationRlsContext.ApplyCommandAsync(
                database.Database,
                actor,
                null,
                cancellationToken).ConfigureAwait(false);
            var connection = database.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText =
                "SELECT notifications.select_agent_channel_company("
                + "@inbound_id, @actor_user_id, @company_id, @request_id)";
            AddParameter(command, "inbound_id", request.InboundMessageId);
            AddParameter(command, "actor_user_id", actor.UserId);
            AddParameter(command, "company_id", request.CompanyId);
            AddParameter(command, "request_id", request.RequestId);
            var status = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return status switch
            {
                "selected" or "replay" => Results.Ok(new { status = "selected" }),
                "idempotency_conflict" => Results.Json(
                    new { code = "idempotency_conflict" },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(
                    new { code = "selection_unavailable" },
                    statusCode: StatusCodes.Status404NotFound),
            };
        }
        catch (PostgresException exception)
            when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege
                or PostgresErrorCodes.CheckViolation)
        {
            return Results.Json(
                new { code = "selection_unavailable" },
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
