using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Paguai;

public static class PaguaiEndpointMapping
{
    private const int MaximumCreateRequestBodyBytes = 24 * 1024;

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
            .Produces<PaguaiError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<PaguaiError>(StatusCodes.Status403Forbidden)
            .Produces<PaguaiError>(StatusCodes.Status429TooManyRequests)
            .Produces<PaguaiError>(StatusCodes.Status503ServiceUnavailable);

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
            .Produces<PaguaiError>(StatusCodes.Status404NotFound)
            .Produces<PaguaiError>(StatusCodes.Status429TooManyRequests)
            .Produces<PaguaiError>(StatusCodes.Status503ServiceUnavailable);

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
                    if (!HasApplicationJsonContentType(request))
                    {
                        return Error("invalid_request", StatusCodes.Status415UnsupportedMediaType);
                    }

                    JsonDocument? document;
                    try
                    {
                        document = await ReadJsonDocumentAsync(request, cancellationToken);
                    }
                    catch (JsonException)
                    {
                        return Error("invalid_request", StatusCodes.Status400BadRequest);
                    }
                    catch (IOException)
                    {
                        return Error("invalid_request", StatusCodes.Status400BadRequest);
                    }

                    if (document is null)
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
            .WithMetadata(new PaguaiAntiforgeryRequiredMetadata())
            .WithMetadata(new PaguaiJsonRequestBodyMetadata())
            .Produces<PaguaiDraft>(StatusCodes.Status201Created)
            .Produces<PaguaiDraft>(StatusCodes.Status200OK)
            .Produces<PaguaiError>(StatusCodes.Status400BadRequest)
            .Produces<PaguaiError>(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<PaguaiError>(StatusCodes.Status403Forbidden)
            .Produces<PaguaiError>(StatusCodes.Status409Conflict)
            .Produces<PaguaiError>(StatusCodes.Status429TooManyRequests)
            .Produces<PaguaiError>(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static IResult Error(string code, int statusCode) =>
        Results.Json(new { code }, statusCode: statusCode);

    private static bool HasApplicationJsonContentType(HttpRequest request) =>
        request.ContentType is { } contentType
        && string.Equals(
            contentType.Split(';', 2)[0].Trim(),
            "application/json",
            StringComparison.OrdinalIgnoreCase
        );

    private static async Task<JsonDocument?> ReadJsonDocumentAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.ContentLength is > MaximumCreateRequestBodyBytes)
        {
            return null;
        }

        await using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > MaximumCreateRequestBodyBytes)
            {
                return null;
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return JsonDocument.Parse(body.ToArray());
    }
}

public sealed record PaguaiError(string Code);

public sealed class PaguaiAntiforgeryRequiredMetadata;

public sealed class PaguaiJsonRequestBodyMetadata;
