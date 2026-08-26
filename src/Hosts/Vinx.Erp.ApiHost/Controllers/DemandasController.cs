using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.ApiHost.Filters;
using Vinx.Erp.ApiHost.Helpers;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.Models;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.Controllers;

/// <summary>
/// Superfície HTTP do módulo Demandas. Estágios honestos: initiation,
/// execution, awaiting_approval (decisão na fatia approvals) e closed; atraso é
/// derivado, nunca coluna de quadro. Sem movimentação livre — todo avanço é
/// comando com requestId e versão.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting(ApiConstants.ProtectedRateLimitPolicy)]
[Route("demandas")]
[Produces("application/json")]
public sealed class DemandasController(
    DemandasService service,
    DemandasApprovalsService approvals
) : ControllerBase
{
    [HttpGet("board")]
    [ProducesResponseType(typeof(IReadOnlyList<DemandBoardCardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Board(
        [FromQuery] Guid? companyId,
        [FromQuery] string? search,
        [FromQuery] string? priority,
        [FromQuery] Guid? responsibleMembershipId,
        [FromQuery] int limit = 100,
        [FromQuery] string? after = null,
        CancellationToken cancellationToken = default
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetBoardAsync(
            actor,
            companyId.Value,
            search,
            priority,
            responsibleMembershipId,
            limit,
            after,
            cancellationToken
        );
        if (result.State == DemandOperationState.Success && result.BoardPage is not null)
        {
            return Ok(result.BoardPage);
        }

        return ToResult(result);
    }

    [HttpGet("demands/{id:guid}")]
    [ProducesResponseType(typeof(DemandDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Detail(Guid id, [FromQuery] Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.GetDetailAsync(actor, companyId.Value, id, cancellationToken);
        return result.State == DemandOperationState.Success && result.Detail is not null
            ? Ok(result.Detail)
            : ToResult(result);
    }

    [HttpPost("demands")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(DemandConflictResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateDemandRequest model, CancellationToken cancellationToken)
    {
        if (!IsTrimmed(model.Title) || !IsTrimmed(model.Description))
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateAsync(actor, model.ToCommand(), cancellationToken);
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/advance")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Advance(Guid id, [FromBody] AdvanceDemandStageRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.AdvanceStageAsync(actor, id, new(model.CompanyId, model.RequestId, model.ExpectedVersion), cancellationToken);
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/checklist-items/complete")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompleteChecklistItem(Guid id, [FromBody] CompleteChecklistItemRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CompleteChecklistItemAsync(
            actor,
            id,
            new(model.CompanyId, model.RequestId, model.ExpectedVersion, model.ItemId),
            cancellationToken
        );
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/responsible")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reassign(Guid id, [FromBody] ReassignResponsibleRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.ReassignResponsibleAsync(
            actor,
            id,
            new(model.CompanyId, model.RequestId, model.ExpectedVersion, model.NewResponsibleMembershipId),
            cancellationToken
        );
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/pause")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Pause(Guid id, [FromBody] PauseAutomationRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.PauseAutomationAsync(actor, id, new(model.CompanyId, model.RequestId, model.ExpectedVersion), cancellationToken);
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/resume")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, [FromBody] ResumeAutomationRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.ResumeAutomationAsync(actor, id, new(model.CompanyId, model.RequestId, model.ExpectedVersion), cancellationToken);
        return ToResult(result);
    }

    /// <summary>Retomada de atrasada: exige novo prazo futuro e reinicia no início.</summary>
    [HttpPost("demands/{id:guid}/resume-overdue")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResumeOverdue(Guid id, [FromBody] ResumeOverdueRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.ResumeOverdueAsync(
            actor,
            id,
            new(model.CompanyId, model.RequestId, model.ExpectedVersion, model.NewDueAt),
            cancellationToken
        );
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/participants")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddParticipant(Guid id, [FromBody] AddParticipantRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.AddParticipantAsync(
            actor,
            id,
            new(model.CompanyId, model.RequestId, model.ExpectedVersion, model.UserId, model.AsSupervisor),
            cancellationToken
        );
        return ToResult(result);
    }

    [HttpDelete("demands/{id:guid}/participants/{participantId:guid}")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveParticipant(
        Guid id,
        Guid participantId,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? requestId,
        [FromQuery] long? expectedVersion,
        CancellationToken cancellationToken
    )
    {
        if (
            companyId is null
            || requestId is null
            || expectedVersion is null or < 1
            || Request.Headers.ContainsKey("content-type")
        )
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.RemoveParticipantAsync(
            actor,
            id,
            new(companyId.Value, requestId.Value, expectedVersion.Value, participantId),
            cancellationToken
        );
        return ToResult(result);
    }

    [HttpGet("routines")]
    [ProducesResponseType(typeof(IReadOnlyList<RoutineResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Routines(
        [FromQuery] Guid? companyId,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default
    )
    {
        if (companyId is null)
        {
            return ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.ListRoutinesAsync(actor, companyId.Value, includeInactive, cancellationToken);
        return ToResult(result);
    }

    [HttpPost("routines")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(RoutineResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RoutineResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateRoutine([FromBody] CreateRoutineRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CreateRoutineAsync(actor, model.ToCommand(), cancellationToken);
        return ToResult(result);
    }

    [HttpPost("routines/{id:guid}/status")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(RoutineResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateRoutineStatus(Guid id, [FromBody] UpdateRoutineStatusRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.UpdateRoutineStatusAsync(
            actor,
            id,
            new(model.CompanyId, model.RequestId, model.ExpectedVersion, model.IsActive),
            cancellationToken
        );
        return ToResult(result);
    }

    /// <summary>Decisão do estágio awaiting_approval consumindo Foundation Approvals.</summary>
    [HttpPost("demands/{id:guid}/approval/approve")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(Guid id, [FromBody] PauseAutomationRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await approvals.DecideAsync(
            actor,
            id,
            approve: true,
            new DemandasApprovalsService.DecisionCommand(model.CompanyId, model.RequestId, model.ExpectedVersion),
            cancellationToken
        );
        return ToResult(result);
    }

    [HttpPost("demands/{id:guid}/approval/reject")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [StrictJsonBody(ApiConstants.MaximumJsonBodyBytes)]
    [ProducesResponseType(typeof(DemandResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] PauseAutomationRequest model, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await approvals.DecideAsync(
            actor,
            id,
            approve: false,
            new DemandasApprovalsService.DecisionCommand(model.CompanyId, model.RequestId, model.ExpectedVersion),
            cancellationToken
        );
        return ToResult(result);
    }

    private IActionResult ToResult(DemandOperationResult result) =>
        result.State switch
        {
            DemandOperationState.Created when result.Demand is not null => StatusCode(
                StatusCodes.Status201Created,
                result.Demand
            ),
            DemandOperationState.Created when result.Routine is not null => StatusCode(
                StatusCodes.Status201Created,
                result.Routine
            ),
            DemandOperationState.Replay or DemandOperationState.Success => Ok(
                result.Demand ?? result.Routine ?? (object?)result.BoardPage?.Items ?? result.Detail
                    ?? (object?)result.Routines
            ),
            DemandOperationState.Invalid or DemandOperationState.RoutineInvalid =>
                ApiControllerError.Error("invalid_request", StatusCodes.Status400BadRequest),
            DemandOperationState.ResumptionDeadlineInvalid => ApiControllerError.Error(
                "resumption_deadline_invalid",
                StatusCodes.Status400BadRequest
            ),
            DemandOperationState.MembershipRequired => ApiControllerError.Error(
                "membership_required",
                StatusCodes.Status403Forbidden
            ),
            DemandOperationState.CapabilityDenied => ApiControllerError.Error(
                "capability_denied",
                StatusCodes.Status403Forbidden
            ),
            DemandOperationState.ResourceNotFound => ApiControllerError.Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            DemandOperationState.IllegalTransition => ApiControllerError.Error(
                "illegal_transition",
                StatusCodes.Status409Conflict
            ),
            DemandOperationState.IdempotencyConflict => ApiControllerError.Error(
                "idempotency_conflict",
                StatusCodes.Status409Conflict
            ),
            DemandOperationState.IntegrityConflict => StatusCode(
                StatusCodes.Status409Conflict,
                new DemandConflictResponse("integrity_conflict", result.Current)
            ),
            _ => ApiControllerError.Error("demandas_unavailable", StatusCodes.Status503ServiceUnavailable),
        };

    private static bool IsTrimmed(string value) =>
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
