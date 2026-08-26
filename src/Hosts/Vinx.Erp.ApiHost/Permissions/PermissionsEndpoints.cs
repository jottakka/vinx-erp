using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Permissions;

namespace Vinx.Erp.ApiHost.Permissions;

/// <summary>
/// Superfície de leitura do diretório de permissões: membros com papéis e
/// capabilities, e papéis com grants. Autorização e gate de capability ficam na
/// função SQL (403 sem oráculo de existência); nenhuma resposta é cacheável e o
/// email do usuário nunca é exposto.
/// </summary>
internal static class PermissionsEndpoints
{
    /// <summary>Mapeia as rotas de permissões dentro do grupo /foundation protegido.</summary>
    public static RouteGroupBuilder MapFoundationPermissionsEndpoints(this RouteGroupBuilder foundation)
    {
        ArgumentNullException.ThrowIfNull(foundation);

        foundation
            .MapGet(
                "/companies/{companyId:guid}/members",
                async (
                    Guid companyId,
                    HttpContext httpContext,
                    CompanyPermissionDirectoryService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    var result = await service.ListMembersAsync(
                        httpContext.GetRequiredActorContext(),
                        companyId,
                        cancellationToken
                    );
                    return ToResult(result.State, result.Items);
                }
            )
            .WithName("CompanyMemberPermissionsList")
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces<IReadOnlyList<CompanyMemberPermissionsView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        foundation
            .MapGet(
                "/companies/{companyId:guid}/roles",
                async (
                    Guid companyId,
                    HttpContext httpContext,
                    CompanyPermissionDirectoryService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    var result = await service.ListRolesAsync(
                        httpContext.GetRequiredActorContext(),
                        companyId,
                        cancellationToken
                    );
                    return ToResult(result.State, result.Items);
                }
            )
            .WithName("CompanyRolePermissionsList")
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces<IReadOnlyList<CompanyRolePermissionsView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        return foundation;
    }

    // Sem distinção entre empresa inexistente e sem capability: ambas convergem no
    // mesmo 403 {code=capability_denied}.
    private static IResult ToResult<T>(CompanyPermissionDirectoryReadState state, IReadOnlyList<T>? items) =>
        state switch
        {
            CompanyPermissionDirectoryReadState.Succeeded => Results.Ok(items),
            CompanyPermissionDirectoryReadState.PermissionDenied => Results.Json(
                new AuthorizationErrorResponse("capability_denied"),
                statusCode: StatusCodes.Status403Forbidden
            ),
            _ => Results.Json(
                new AuthorizationErrorResponse("audit_unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable
            ),
        };
}
