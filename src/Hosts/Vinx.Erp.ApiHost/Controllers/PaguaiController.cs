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
public sealed class PaguaiController(
    PaguaiDraftService service,
    PaguaiProposalService proposalService,
    PaguaiProposalConfirmationService proposalConfirmationService) : ControllerBase
{
    [HttpGet("draft-proposals/{id:guid}")]
    [ProducesResponseType(typeof(PaguaiProposal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetProposal(
        Guid id,
        [FromQuery] Guid? companyId,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await proposalService.GetAsync(
            actor, companyId ?? Guid.Empty, id, cancellationToken);
        return result.State switch
        {
            PaguaiProposalOperationState.Found => Ok(result.Proposal),
            PaguaiProposalOperationState.NotFound => ApiControllerError.Error(
                "resource_not_found", StatusCodes.Status404NotFound),
            PaguaiProposalOperationState.ProposalUnavailable => ApiControllerError.Error(
                "proposal_unavailable", StatusCodes.Status409Conflict),
            _ => ApiControllerError.Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
        };
    }

    [HttpPost("draft-proposals/{id:guid}/confirm")]
    [Authorize(Policy = BrowserBffAuthenticationExtensions.BrowserSessionPolicyName)]
    [BrowserBffCookieOnlyEndpointMetadata]
    [PaguaiAntiforgeryRequiredMetadata]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(PaguaiDraft), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PaguaiDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ConfirmProposal(
        Guid id,
        [FromBody] ConfirmPaguaiProposalRequest model,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await proposalConfirmationService.ConfirmAsync(
            actor, model.ToCommand(id), cancellationToken);
        return result.State switch
        {
            PaguaiProposalOperationState.Created => CreatedAtAction(
                nameof(Detail),
                new { id = result.Draft!.Id, companyId = result.Draft.CompanyId },
                result.Draft),
            PaguaiProposalOperationState.Replay => Ok(result.Draft),
            PaguaiProposalOperationState.Invalid => ApiControllerError.InvalidRequest(
                StatusCodes.Status400BadRequest),
            PaguaiProposalOperationState.IdempotencyConflict => ApiControllerError.Error(
                "idempotency_conflict", StatusCodes.Status409Conflict),
            PaguaiProposalOperationState.ProposalUnavailable => ApiControllerError.Error(
                "proposal_unavailable", StatusCodes.Status409Conflict),
            _ => ApiControllerError.Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
        };
    }

    [HttpPost("draft-proposals/{id:guid}/reject")]
    [Authorize(Policy = BrowserBffAuthenticationExtensions.BrowserSessionPolicyName)]
    [BrowserBffCookieOnlyEndpointMetadata]
    [PaguaiAntiforgeryRequiredMetadata]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RejectProposal(
        Guid id,
        [FromBody] RejectPaguaiProposalRequest model,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await proposalConfirmationService.RejectAsync(
            actor, model.ToCommand(id), cancellationToken);
        return result.State switch
        {
            PaguaiProposalOperationState.Created or PaguaiProposalOperationState.Replay => NoContent(),
            PaguaiProposalOperationState.Invalid => ApiControllerError.InvalidRequest(
                StatusCodes.Status400BadRequest),
            PaguaiProposalOperationState.IdempotencyConflict => ApiControllerError.Error(
                "idempotency_conflict", StatusCodes.Status409Conflict),
            PaguaiProposalOperationState.ProposalUnavailable => ApiControllerError.Error(
                "proposal_unavailable", StatusCodes.Status409Conflict),
            _ => ApiControllerError.Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
        };
    }

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

    [HttpPost("requests/from-document")]
    [PaguaiAntiforgeryRequiredMetadata]
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
    public async Task<IActionResult> CreateFromDocument(
        [FromBody] CreatePaguaiDraftFromDocumentRequest model,
        CancellationToken cancellationToken
    )
    {
        if (!IsTrimmed(model.Title) || !IsTrimmed(model.Description))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateFromInboundDocumentAsync(
            actor,
            model.ToCommand(),
            cancellationToken
        );
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
            PaguaiDraftState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
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

    [HttpPost("requests")]
    [PaguaiAntiforgeryRequiredMetadata]
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
        if (
            !IsTrimmed(model.Title)
            || !IsTrimmed(model.Description)
            || !IsTrimmed(model.BeneficiaryDisplayName)
            || !PaguaiAmountRules.IsValid(model.Amount)
            || model.DueDate == default
        )
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
            PaguaiDraftState.Invalid => ApiControllerError.Error(
                "invalid_request",
                StatusCodes.Status400BadRequest
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
