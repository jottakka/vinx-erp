using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost;

internal static class AtendimentoHttpContract
{
    public static IResult ToQueueResult(AtendimentoCardOperationResult result) =>
        result.State switch
        {
            AtendimentoCardOperationState.Success => Results.Ok(result.Cards),
            AtendimentoCardOperationState.Invalid => Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoCardOperationState.MembershipRequired => Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.CapabilityDenied => Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            _ => Error("atendimento_unavailable", StatusCodes.Status503ServiceUnavailable),
        };

    public static IResult ToCommandResult(AtendimentoCardOperationResult result) =>
        result.State switch
        {
            AtendimentoCardOperationState.Created => Results.Json(
                result.Card,
                statusCode: StatusCodes.Status201Created
            ),
            AtendimentoCardOperationState.Replay or AtendimentoCardOperationState.Success =>
                Results.Ok(result.Card),
            AtendimentoCardOperationState.Invalid => Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoCardOperationState.MembershipRequired => Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.CapabilityDenied => Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.ResourceNotFound => Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            AtendimentoCardOperationState.IdempotencyConflict => Error(
                "idempotency_conflict",
                StatusCodes.Status409Conflict
            ),
            AtendimentoCardOperationState.IntegrityConflict => Results.Json(
                new { code = "integrity_conflict", current = result.Current },
                statusCode: StatusCodes.Status409Conflict
            ),
            _ => Error("atendimento_unavailable", StatusCodes.Status503ServiceUnavailable),
        };

    public static IResult Error(string code, int statusCode) =>
        Results.Json(new { code }, statusCode: statusCode);
}
