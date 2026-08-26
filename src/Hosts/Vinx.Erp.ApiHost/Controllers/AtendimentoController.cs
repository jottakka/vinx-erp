using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.ApiHost.Filters;
using Vinx.Erp.ApiHost.Helpers;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.Models;
using Vinx.Erp.Atendimento;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting(ApiConstants.ProtectedRateLimitPolicy)]
[Route("atendimento")]
[Produces("application/json")]
public sealed class AtendimentoController(AtendimentoCardService service) : ControllerBase
{
    [HttpGet("cards")]
    [ProducesResponseType(typeof(IReadOnlyList<AtendimentoCardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Queue(
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetQueueAsync(actor, companyId.Value, cancellationToken);
        return result.State switch
        {
            AtendimentoCardOperationState.Success => Ok(result.Cards),
            AtendimentoCardOperationState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoCardOperationState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpGet("assignment-options")]
    [ProducesResponseType(
        typeof(IReadOnlyList<AtendimentoAssignmentOptionResponse>),
        StatusCodes.Status200OK
    )]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AssignmentOptions(
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetAssignmentOptionsAsync(actor, companyId.Value, cancellationToken);
        return result.State switch
        {
            AtendimentoCardOperationState.Success => Ok(result.AssignmentOptions),
            AtendimentoCardOperationState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoCardOperationState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpPost("cards")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAtendimentoCardRequest model,
        CancellationToken cancellationToken
    )
    {
        if (!IsTrimmed(model.Title) || !IsTrimmed(model.Description))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateAsync(actor, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/assign")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AtendimentoCardConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Assign(
        Guid id,
        [FromBody] AssignAtendimentoCardRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.AssignAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/move")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AtendimentoCardConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Move(
        Guid id,
        [FromBody] MoveAtendimentoCardRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        if (!IsTrimmed(model.Column))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.MoveAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/cancel")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AtendimentoCardConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelAtendimentoCardRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        if (!IsTrimmed(model.Reason))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CancelAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    private IActionResult ToCommandResult(AtendimentoCardOperationResult result) =>
        result.State switch
        {
            AtendimentoCardOperationState.Created => StatusCode(
                StatusCodes.Status201Created,
                result.Card
            ),
            AtendimentoCardOperationState.Replay or AtendimentoCardOperationState.Success => Ok(
                result.Card
            ),
            AtendimentoCardOperationState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoCardOperationState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoCardOperationState.ResourceNotFound => ApiControllerError.Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            AtendimentoCardOperationState.IdempotencyConflict => ApiControllerError.Error(
                "idempotency_conflict",
                StatusCodes.Status409Conflict
            ),
            AtendimentoCardOperationState.IntegrityConflict => StatusCode(
                StatusCodes.Status409Conflict,
                new AtendimentoCardConflictResponse("integrity_conflict", result.Current)
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };

    private static bool IsTrimmed(string value) =>
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
