using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost;

/// <summary>Exige GUID ausente apenas quando null; rejeita o valor padrão (vazio).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NonEmptyGuidAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        value is null || (value is Guid guid && guid != Guid.Empty)
            ? ValidationResult.Success
            : new ValidationResult(ErrorMessage ?? $"{context.DisplayName} must be a non-empty UUID.");
}

/// <summary>
/// Validação estrita de fronteira antes do binding: content-type JSON, limite de bytes
/// efetivamente lidos (inclusive chunked) e rewinding do corpo para o model binder.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictJsonBodyAttribute
    : Attribute,
        Microsoft.AspNetCore.Mvc.Filters.IAsyncResourceFilter
{
    private readonly int _maximumBytes;

    public StrictJsonBodyAttribute(int maximumBytes)
    {
        _maximumBytes = maximumBytes;
    }

    public async Task OnResourceExecutionAsync(
        Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ResourceExecutionDelegate next
    )
    {
        var request = context.HttpContext.Request;
        var contentType = request.ContentType?.Split(';', 2)[0].Trim();
        if (
            !string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase)
        )
        {
            context.Result = ApiControllerError.InvalidRequest(
                StatusCodes.Status415UnsupportedMediaType
            );
            return;
        }

        request.EnableBuffering();
        var total = 0L;
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > _maximumBytes)
            {
                context.Result = ApiControllerError.InvalidRequest(
                    StatusCodes.Status400BadRequest
                );
                return;
            }
        }

        request.Body.Seek(0, SeekOrigin.Begin);
        await next();
    }
}

internal static class ApiControllerError
{
    public static IActionResult InvalidRequest(int statusCode) =>
        new ObjectResult(new { code = "invalid_request" }) { StatusCode = statusCode };

    public static IActionResult Error(string code, int statusCode) =>
        new ObjectResult(new { code }) { StatusCode = statusCode };
}

public sealed class CreatePaguaiDraftRequest
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

    public CreateDraftCommand ToCommand() =>
        new(CompanyId!.Value, RequestId!.Value, Title!, Description!);
}

[ApiController]
[Authorize]
[EnableRateLimiting("foundation")]
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
            _ => ApiControllerError.Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
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
            _ => ApiControllerError.Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
        };
    }

    [HttpPost("requests")]
    [PaguaiAntiforgeryRequiredMetadata]
    [PaguaiJsonRequestBodyMetadata]
    [StrictJsonBody(24 * 1024)]
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
            !IsTrimmed(model.Title!)
            || !IsTrimmed(model.Description!)
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
            PaguaiDraftState.IdempotencyConflict => ApiControllerError.Error(
                "idempotency_conflict",
                StatusCodes.Status409Conflict
            ),
            _ => ApiControllerError.Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static bool IsTrimmed(string value) =>
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
