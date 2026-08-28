using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost.Identity;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Documenta os requisitos de segurança das operações autenticadas: Bearer +
/// BrowserSessionCookie nas rotas comuns e somente o cookie nas rotas
/// exclusivas da sessão browser.
/// </summary>
public sealed class SecurityRequirementsOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (
            metadata.OfType<IAllowAnonymous>().Any()
            || !metadata.OfType<IAuthorizeData>().Any()
        )
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        if (metadata.OfType<BrowserBffCookieOnlyEndpointMetadata>().Any())
        {
            operation.Security.Add(
                new OpenApiSecurityRequirement
                {
                    [
                        new OpenApiSecuritySchemeReference(
                            BrowserBffAuthenticationExtensions.CookieScheme,
                            context.Document,
                            null
                        )
                    ] = [],
                }
            );
            return Task.CompletedTask;
        }

        operation.Security.Add(
            new OpenApiSecurityRequirement
            {
                [
                    new OpenApiSecuritySchemeReference(
                        "Bearer",
                        context.Document,
                        null
                    )
                ] = [],
            }
        );
        operation.Security.Add(
            new OpenApiSecurityRequirement
            {
                [
                    new OpenApiSecuritySchemeReference(
                        BrowserBffAuthenticationExtensions.CookieScheme,
                        context.Document,
                        null
                    )
                ] = [],
            }
        );
        return Task.CompletedTask;
    }
}
