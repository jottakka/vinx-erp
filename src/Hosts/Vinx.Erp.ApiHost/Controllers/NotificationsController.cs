using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.ApiHost.Helpers;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.Models;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Notifications;

namespace Vinx.Erp.ApiHost.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting(ApiConstants.ProtectedRateLimitPolicy)]
[Route("notifications")]
[Produces("application/json")]
public sealed class NotificationsController(NotificationInboxService service)
    : ControllerBase
{
    [HttpGet("inbox")]
    [ProducesResponseType(typeof(NotificationInboxPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Inbox(
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 50)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.ListAsync(actor, cursor, limit, cancellationToken);
        return result.State switch
        {
            NotificationInboxState.Listed => Ok(
                new NotificationInboxPageResponse(
                    result.Page!.Items.Select(ToItemResponse).ToList(),
                    result.Page.NextCursor
                )
            ),
            NotificationInboxState.Invalid => ApiControllerError.InvalidRequest(
                StatusCodes.Status400BadRequest
            ),
            // 401 é corpo vazio por contrato, inclusive no caminho defensivo.
            NotificationInboxState.Unauthorized => StatusCode(
                StatusCodes.Status401Unauthorized
            ),
            _ => ApiControllerError.Error(
                "notifications_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpGet("inbox/unread-count")]
    [ProducesResponseType(typeof(NotificationUnreadCountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.CountUnreadAsync(actor, cancellationToken);
        return result.State switch
        {
            NotificationUnreadCountState.Counted => Ok(
                new NotificationUnreadCountResponse(result.UnreadCount)
            ),
            NotificationUnreadCountState.Unauthorized => StatusCode(
                StatusCodes.Status401Unauthorized
            ),
            _ => ApiControllerError.Error(
                "notifications_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    [HttpPost("inbox/{deliveryId:guid}/read")]
    [BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]
    [ProducesResponseType(typeof(NotificationMarkReadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> MarkRead(
        Guid deliveryId,
        CancellationToken cancellationToken
    )
    {
        if (deliveryId == Guid.Empty)
        {
            return ApiControllerError.InvalidRequest(StatusCodes.Status400BadRequest);
        }

        var actor = HttpContext.GetRequiredActorContext();
        var result = await service.MarkReadAsync(actor, deliveryId, cancellationToken);
        return result.State switch
        {
            NotificationMarkReadState.Read => Ok(new NotificationMarkReadResponse("read")),
            NotificationMarkReadState.NotFound => ApiControllerError.Error(
                "resource_not_found",
                StatusCodes.Status404NotFound
            ),
            NotificationMarkReadState.Invalid => ApiControllerError.InvalidRequest(
                StatusCodes.Status400BadRequest
            ),
            NotificationMarkReadState.Unauthorized => StatusCode(
                StatusCodes.Status401Unauthorized
            ),
            _ => ApiControllerError.Error(
                "notifications_unavailable",
                StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    private static NotificationInboxItemResponse ToItemResponse(
        NotificationInboxItem item
    ) =>
        new(
            item.Id,
            item.CompanyId,
            item.Source,
            item.Event,
            item.CreatedAt,
            item.ReadAt,
            item.Action is null
                ? null
                : new NotificationInboxActionResponse(item.Action.Kind, item.Action.Href)
        );
}
