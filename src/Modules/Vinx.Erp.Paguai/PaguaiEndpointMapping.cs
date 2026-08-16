using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Paguai;

public static class PaguaiEndpointMapping
{
    public static IEndpointRouteBuilder MapPaguaiEndpoints(
        this IEndpointRouteBuilder endpoints,
        string rateLimitPolicy
    )
    {
        var paguai = endpoints
            .MapGroup("/paguai")
            .RequireAuthorization()
            .RequireRateLimiting(rateLimitPolicy);
        paguai
            .MapGet(
                "/requests",
                async (
                    Guid? companyId,
                    HttpContext context,
                    PaguaiDraftService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    var result = await service.ListAsync(
                        context.GetRequiredActorContext(),
                        companyId ?? Guid.Empty,
                        cancellationToken
                    );
                    return result.State switch
                    {
                        PaguaiDraftState.Created => Results.Ok(result.Drafts),
                        PaguaiDraftState.Invalid => Error(
                            "invalid_request",
                            StatusCodes.Status400BadRequest
                        ),
                        PaguaiDraftState.Forbidden => Error(
                            "capability_denied",
                            StatusCodes.Status403Forbidden
                        ),
                        _ => Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
                    };
                }
            )
            .WithName("PaguaiDraftList")
            .Produces<IReadOnlyList<PaguaiDraft>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        paguai
            .MapGet(
                "/requests/{id:guid}",
                async (
                    Guid id,
                    Guid? companyId,
                    HttpContext context,
                    PaguaiDraftService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    var result = await service.GetAsync(
                        context.GetRequiredActorContext(),
                        companyId ?? Guid.Empty,
                        id,
                        cancellationToken
                    );
                    return result.State switch
                    {
                        PaguaiDraftState.Created => Results.Ok(result.Draft),
                        PaguaiDraftState.NotFound => Error(
                            "resource_not_found",
                            StatusCodes.Status404NotFound
                        ),
                        _ => Error("paguai_unavailable", StatusCodes.Status503ServiceUnavailable),
                    };
                }
            )
            .WithName("PaguaiDraftDetail")
            .Produces<PaguaiDraft>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        paguai
            .MapPost(
                "/requests",
                async (
                    HttpRequest request,
                    HttpContext context,
                    PaguaiDraftService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    JsonDocument document;
                    try
                    {
                        document = await JsonDocument.ParseAsync(
                            request.Body,
                            cancellationToken: cancellationToken
                        );
                    }
                    catch (JsonException)
                    {
                        return Error("invalid_request", StatusCodes.Status400BadRequest);
                    }
                    catch (IOException)
                    {
                        return Error("invalid_request", StatusCodes.Status400BadRequest);
                    }

                    using (document)
                    {
                        if (
                            !PaguaiDraftHttpContract.TryParse(document.RootElement, out var command)
                        )
                        {
                            return Error("invalid_request", StatusCodes.Status400BadRequest);
                        }

                        var result = await service.CreateAsync(
                            context.GetRequiredActorContext(),
                            command,
                            cancellationToken
                        );
                        return result.State switch
                        {
                            PaguaiDraftState.Created => Results.Created(
                                $"/paguai/requests/{result.Draft!.Id}?companyId={result.Draft.CompanyId}",
                                result.Draft
                            ),
                            PaguaiDraftState.Replay => Results.Ok(result.Draft),
                            PaguaiDraftState.Forbidden => Error(
                                "capability_denied",
                                StatusCodes.Status403Forbidden
                            ),
                            PaguaiDraftState.IdempotencyConflict => Error(
                                "idempotency_conflict",
                                StatusCodes.Status409Conflict
                            ),
                            _ => Error(
                                "paguai_unavailable",
                                StatusCodes.Status503ServiceUnavailable
                            ),
                        };
                    }
                }
            )
            .WithName("PaguaiDraftCreate")
            .Produces<PaguaiDraft>(StatusCodes.Status201Created)
            .Produces<PaguaiDraft>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static IResult Error(string code, int statusCode) =>
        Results.Json(new { code }, statusCode: statusCode);
}
