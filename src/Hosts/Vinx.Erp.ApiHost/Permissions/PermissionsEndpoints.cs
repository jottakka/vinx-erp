using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vinx.Erp.ApiHost.Contracts;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.Models;
using Vinx.Erp.ApiHost.OpenApi;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Permissions;

namespace Vinx.Erp.ApiHost.Permissions;

/// <summary>
/// Superfície do diretório de permissões: leitura de membros com papéis e
/// capabilities, papéis com grants, e mutações de atribuição (assign/unassign
/// com bloqueio de último administrador) e cópia de permissões entre membresias.
/// Nenhuma resposta é cacheável; mutações exigem antiforgery cookie + strict JSON.
/// </summary>
internal static class PermissionsEndpoints
{
    private const int MaximumPermissionsRequestBodyBytes = 8 * 1024;

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

        foundation
            .MapPost(
                "/companies/{companyId:guid}/role-assignments",
                async (
                    Guid companyId,
                    HttpRequest request,
                    HttpContext httpContext,
                    RoleAssignmentService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    using var document = await TryReadJsonDocumentAsync(request, cancellationToken);
                    if (
                        document is null
                        || !PermissionsHttpContract.TryParseAssignment(
                            document.RootElement,
                            out var requestId,
                            out var membershipId,
                            out var roleId)
                    )
                        return InvalidRequest();

                    var result = await service.AssignRoleToMembershipAsync(
                        httpContext.GetRequiredActorContext(),
                        new AssignRoleToMembershipCommand(companyId, roleId, membershipId, requestId),
                        cancellationToken
                    );
                    return result.ToMinimalApiResult();
                }
            )
            .WithName("CompanyRoleAssignmentCreate")
            .WithMetadata(new JsonRequestBodyContract(typeof(AssignRoleToMembershipCommand)))
            .WithMetadata(new BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true))
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces<RoleAssignmentCreatedResponse>(StatusCodes.Status200OK)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        foundation
            .MapPost(
                "/companies/{companyId:guid}/role-unassignments",
                async (
                    Guid companyId,
                    HttpRequest request,
                    HttpContext httpContext,
                    RoleUnassignmentService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    using var document = await TryReadJsonDocumentAsync(request, cancellationToken);
                    if (
                        document is null
                        || !PermissionsHttpContract.TryParseAssignment(
                            document.RootElement,
                            out var requestId,
                            out var membershipId,
                            out var roleId)
                    )
                        return InvalidRequest();

                    var result = await service.UnassignRoleFromMembershipAsync(
                        httpContext.GetRequiredActorContext(),
                        new UnassignRoleFromMembershipCommand(companyId, roleId, membershipId, requestId),
                        cancellationToken
                    );
                    return result.ToMinimalApiResult();
                }
            )
            .WithName("CompanyRoleUnassignmentCreate")
            .WithMetadata(new JsonRequestBodyContract(typeof(UnassignRoleFromMembershipCommand)))
            .WithMetadata(new BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true))
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces<UnassignRoleFromMembershipResult>(StatusCodes.Status200OK)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        foundation
            .MapPost(
                "/companies/{companyId:guid}/permission-copies",
                async (
                    Guid companyId,
                    HttpRequest request,
                    HttpContext httpContext,
                    RolePermissionsCopyService service,
                    CancellationToken cancellationToken
                ) =>
                {
                    using var document = await TryReadJsonDocumentAsync(request, cancellationToken);
                    if (
                        document is null
                        || !PermissionsHttpContract.TryParseCopy(
                            document.RootElement,
                            out var requestId,
                            out var sourceMembershipId,
                            out var targetMembershipId)
                    )
                        return InvalidRequest();

                    var result = await service.CopyAsync(
                        httpContext.GetRequiredActorContext(),
                        new CopyRolePermissionsCommand(
                            companyId,
                            sourceMembershipId,
                            targetMembershipId,
                            requestId),
                        cancellationToken
                    );
                    return result.State switch
                    {
                        RolePermissionsCopyState.Invalid => InvalidRequest(),
                        _ => result.Outcome!.ToMinimalApiResult(),
                    };
                }
            )
            .WithName("CompanyPermissionCopyCreate")
            .WithMetadata(new JsonRequestBodyContract(typeof(CopyRolePermissionsCommand)))
            .WithMetadata(new BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true))
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces<CopyRolePermissionsResult>(StatusCodes.Status200OK)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status409Conflict)
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

    private static IResult InvalidRequest() =>
        Results.Json(
            new AuthorizationErrorResponse("invalid_request"),
            statusCode: StatusCodes.Status400BadRequest
        );

    /// <summary>
    /// Lê o corpo JSON com teto de 8 KiB e parse estrito; qualquer violação
    /// (content-type, tamanho, JSON malformado ou leitura interrompida) vira null.
    /// </summary>
    private static async Task<System.Text.Json.JsonDocument?> TryReadJsonDocumentAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!request.HasJsonContentType())
            return null;
        var body = new byte[MaximumPermissionsRequestBodyBytes + 1];
        var bodyLength = 0;
        try
        {
            int bytesRead;
            do
            {
                bytesRead = await request.Body.ReadAsync(body.AsMemory(bodyLength), cancellationToken);
                bodyLength += bytesRead;
            } while (bytesRead != 0 && bodyLength <= MaximumPermissionsRequestBodyBytes);
        }
        catch (IOException)
        {
            return null;
        }

        if (bodyLength > MaximumPermissionsRequestBodyBytes)
            return null;
        try
        {
            return System.Text.Json.JsonDocument.Parse(body.AsMemory(0, bodyLength));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static class PermissionsHttpContract
    {
        private static readonly HashSet<string> AssignmentFields =
            ["requestId", "membershipId", "roleId"];
        private static readonly HashSet<string> CopyFields =
            ["requestId", "sourceMembershipId", "targetMembershipId"];

        public static bool TryParseAssignment(
            System.Text.Json.JsonElement body,
            out Guid requestId,
            out Guid membershipId,
            out Guid roleId
        )
        {
            requestId = default;
            membershipId = default;
            roleId = default;
            if (!StrictJsonFieldSet.TryReadExact(body, AssignmentFields, out var values))
                return false;
            if (
                !values["requestId"].TryGetGuid(out requestId)
                || !values["membershipId"].TryGetGuid(out membershipId)
                || !values["roleId"].TryGetGuid(out roleId)
            )
                return false;
            return true;
        }

        public static bool TryParseCopy(
            System.Text.Json.JsonElement body,
            out Guid requestId,
            out Guid sourceMembershipId,
            out Guid targetMembershipId
        )
        {
            requestId = default;
            sourceMembershipId = default;
            targetMembershipId = default;
            if (!StrictJsonFieldSet.TryReadExact(body, CopyFields, out var values))
                return false;
            if (
                !values["requestId"].TryGetGuid(out requestId)
                || !values["sourceMembershipId"].TryGetGuid(out sourceMembershipId)
                || !values["targetMembershipId"].TryGetGuid(out targetMembershipId)
            )
                return false;
            return true;
        }
    }
}
