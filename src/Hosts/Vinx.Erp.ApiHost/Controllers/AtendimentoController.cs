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
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] Guid? assigneeMembershipId,
        [FromQuery] bool? overdue,
        [FromQuery] string? search,
        CancellationToken cancellationToken
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        AtendimentoCardQueueFilter? filter =
            status is null
            && priority is null
            && assigneeMembershipId is null
            && overdue is not true
            && string.IsNullOrWhiteSpace(search)
                ? null
                : new(
                    status,
                    priority,
                    assigneeMembershipId,
                    overdue == true,
                    string.IsNullOrWhiteSpace(search) ? null : search
                );
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetQueueAsync(actor, companyId.Value, filter, cancellationToken);
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
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
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
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
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
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
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
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
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

    /// <summary>Atualiza campos de ticket; corpo vazio de campo significa inalterado.</summary>
    [HttpPost("cards/{id:guid}/fields")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AtendimentoCardConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Fields(
        Guid id,
        [FromBody] UpdateAtendimentoCardFieldsRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.UpdateFieldsAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/watchers/add")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AtendimentoCardConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AddWatcher(
        Guid id,
        [FromBody] AtendimentoWatcherRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.AddWatcherAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/watchers/remove")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AtendimentoCardConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RemoveWatcher(
        Guid id,
        [FromBody] AtendimentoWatcherRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.RemoveWatcherAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    /// <summary>Timeline do cartão: últimos 100 eventos em ordem crescente.</summary>
    [HttpGet("cards/{id:guid}/events")]
    [ProducesResponseType(typeof(IReadOnlyList<AtendimentoTimelineEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Events(
        Guid id,
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetTimelineAsync(actor, companyId.Value, id, cancellationToken);
        return result.State switch
        {
            AtendimentoTimelineReadState.Success => Ok(result.Entries),
            AtendimentoTimelineReadState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoTimelineReadState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoTimelineReadState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoTimelineReadState.ResourceNotFound => ApiControllerError.Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    /// <summary>Categorias e canais da empresa para chips do quadro.</summary>
    [HttpGet("lookups")]
    [ProducesResponseType(typeof(AtendimentoLookupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Lookups(
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetLookupsAsync(actor, companyId.Value, cancellationToken);
        return result.State switch
        {
            AtendimentoLookupsReadState.Success => Ok(result.Lookups),
            AtendimentoLookupsReadState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoLookupsReadState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoLookupsReadState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpPost("categories")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCategoryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AtendimentoCategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateCategory(
        [FromBody] CreateAtendimentoCategoryRequest model,
        CancellationToken cancellationToken
    )
    {
        if (!IsTrimmed(model.Name))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateCategoryAsync(actor, model.ToCommand(), cancellationToken);
        return ToLookupResult(result);
    }

    [HttpPost("categories/{id:guid}/deactivate")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(AtendimentoCategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> DeactivateCategory(
        Guid id,
        [FromBody] DeactivateAtendimentoCategoryRequest model,
        CancellationToken cancellationToken
    )
    {
        if (id == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.DeactivateCategoryAsync(actor, model.ToCommand(id), cancellationToken);
        return ToLookupResult(result, conflictCode: null);
    }

    private IActionResult ToCommandResult(AtendimentoCardOperationResult result) =>
        result.State switch
        {
            AtendimentoCardOperationState.Created => CreatedAtAction(
                nameof(Queue),
                new { companyId = result.Card!.CompanyId },
                result.Card
            ),
            AtendimentoCardOperationState.Replay
            or AtendimentoCardOperationState.Success
            or AtendimentoCardOperationState.NoChange => Ok(result.Card),
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
            AtendimentoCardOperationState.TerminalState => StatusCode(
                StatusCodes.Status409Conflict,
                new AtendimentoCardConflictResponse("terminal_state", result.Current)
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };

    private IActionResult ToLookupResult(
        AtendimentoLookupWriteResult result,
        string? conflictCode = "lookup_conflict"
    ) =>
        result.State switch
        {
            AtendimentoLookupWriteState.Created => StatusCode(
                StatusCodes.Status201Created,
                result.Category
            ),
            AtendimentoLookupWriteState.Replay => Ok(result.Category),
            AtendimentoLookupWriteState.Conflict => StatusCode(
                StatusCodes.Status409Conflict,
                new { code = conflictCode ?? "lookup_conflict" }
            ),
            AtendimentoLookupWriteState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            AtendimentoLookupWriteState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoLookupWriteState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            AtendimentoLookupWriteState.ResourceNotFound => ApiControllerError.Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            _ => ApiControllerError.Error(
                "atendimento_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };

    private static bool IsTrimmed(string value) =>
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
