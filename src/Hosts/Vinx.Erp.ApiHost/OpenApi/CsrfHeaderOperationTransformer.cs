using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Documenta o header antiforgery X-CSRF-TOKEN em toda operação marcada com
/// exigência de antiforgery: obrigatório quando exigido sempre (inclusive Bearer)
/// ou quando a operação é exclusiva de cookie de sessão; dispensado para Bearer
/// nas operações cookie-only.
/// </summary>
public sealed class CsrfHeaderOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var antiforgeryRequirement = metadata
            .OfType<IAntiforgeryRequiredMetadata>()
            .FirstOrDefault();
        var isCookieOnlyEndpoint = metadata
            .OfType<BrowserBffCookieOnlyEndpointMetadata>()
            .Any();
        if (antiforgeryRequirement is null && !isCookieOnlyEndpoint)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(
            new OpenApiParameter
            {
                Name = BrowserBffAuthenticationExtensions.AntiforgeryHeaderName,
                In = ParameterLocation.Header,
                Required =
                    isCookieOnlyEndpoint
                    || antiforgeryRequirement is { CookieAuthenticatedOnly: false },
                Description =
                    "Obrigatório quando a operação autenticada usa o cookie de sessão BrowserSessionCookie; dispensado para Bearer.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            }
        );
        return Task.CompletedTask;
    }
}
