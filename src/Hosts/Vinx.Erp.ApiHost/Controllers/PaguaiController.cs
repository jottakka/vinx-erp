using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.ApiHost.Filters;
using Vinx.Erp.ApiHost.Helpers;
using Vinx.Erp.ApiHost.Models;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.Atendimento;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting(ApiConstants.ProtectedRateLimitPolicy)]
[Route("paguai")]
[Produces("application/json")]
public sealed class PaguaiController(PaguaiDraftService service) : ControllerBase
{
    [HttpGet("requests")]
    [ProducesResponseType(typeof(IReadOnlyList<PaguaiDraft>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken
    )
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.ListAsync(actor, companyId ?? Guid.Empty, cancellationToken);
        return result.State switch
        {
            PaguaiDraftState.Created => Ok(result.Drafts),
            PaguaiDraftState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
            ),
            PaguaiDraftState.Forbidden => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            _ => ApiControllerError.Error(
                "paguai_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpGet("requests/{id:guid}")]
    [ProducesResponseType(typeof(PaguaiDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Detail(
        Guid id,
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken
    )
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetAsync(actor, companyId ?? Guid.Empty, id, cancellationToken);
        return result.State switch
        {
            PaguaiDraftState.Created => Ok(result.Draft),
            PaguaiDraftState.NotFound => ApiControllerError.Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            _ => ApiControllerError.Error(
                "paguai_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpPost("requests")]
    [PaguaiAntiforgeryRequiredMetadata]
    [PaguaiJsonRequestBodyMetadata]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(PaguaiDraft), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PaguaiDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(
        [FromBody] CreatePaguaiDraftRequest model,
        CancellationToken cancellationToken
    )
    {
        if (!IsTrimmed(model.Title!) || !IsTrimmed(model.Description!))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateAsync(actor, model.ToCommand(), cancellationToken);
        return result.State switch
        {
            PaguaiDraftState.Created => CreatedAtAction(
                nameof(Detail),
                new { id = result.Draft!.Id, companyId = result.Draft.CompanyId },
                result.Draft
            ),
            PaguaiDraftState.Replay => Ok(result.Draft),
            PaguaiDraftState.Forbidden => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            PaguaiDraftState.IdempotencyConflict => ApiControllerError.Error(
                "idempotency_conflict",
                StatusCodes.Status409Conflict
            ),
            _ => ApiControllerError.Error(
                "paguai_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    private static bool IsTrimmed(string value) =>
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}

internal sealed record AtendimentoCardConflictResponse(
    string Code,
    AtendimentoCardResponse? Current
);

public sealed class CreateAtendimentoCardRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid? CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid? RequestId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string? Title { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(4000)]
    public string? Description { get; init; }

    public CreateAtendimentoCardCommand ToCommand() =>
        new(CompanyId!.Value, RequestId!.Value, Title!, Description!);
}

public sealed class AssignAtendimentoCardRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid? CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid? RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long? ExpectedVersion { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid? AssigneeMembershipId { get; init; }

    public AssignAtendimentoCardCommand ToCommand() =>
        new(
            CompanyId!.Value,
            RequestId!.Value,
            ExpectedVersion!.Value,
            AssigneeMembershipId!.Value
        );
}

public sealed class MoveAtendimentoCardRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid? CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid? RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long? ExpectedVersion { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(30)]
    public string? Column { get; init; }

    public MoveAtendimentoCardCommand ToCommand() =>
        new(CompanyId!.Value, RequestId!.Value, ExpectedVersion!.Value, Column!);
}

[ApiController]
[Authorize]
[EnableRateLimiting("foundation")]
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

    [HttpPost("cards")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(24 * 1024)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AtendimentoCardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAtendimentoCardRequest model,
        CancellationToken cancellationToken
    )
    {
        if (!IsTrimmed(model.Title!) || !IsTrimmed(model.Description!))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateAsync(actor, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/assign")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(24 * 1024)]
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
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.AssignAsync(actor, id, model.ToCommand(), cancellationToken);
        return ToCommandResult(result);
    }

    [HttpPost("cards/{id:guid}/move")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(24 * 1024)]
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
        if (!IsTrimmed(model.Column!))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.MoveAsync(actor, id, model.ToCommand(), cancellationToken);
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
