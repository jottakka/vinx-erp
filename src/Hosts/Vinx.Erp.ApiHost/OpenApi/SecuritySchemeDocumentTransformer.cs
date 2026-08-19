using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost.Identity;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Registra os security schemes usados pela API: Bearer (JWT Supabase) e
/// BrowserSessionCookie (sessão BFF).
/// </summary>
public sealed class SecuritySchemeDocumentTransformer(IHostEnvironment environment)
    : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        document.Components.SecuritySchemes[BrowserBffAuthenticationExtensions.CookieScheme] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = BrowserBffAuthenticationExtensions.GetSessionCookieName(environment),
            };
        return Task.CompletedTask;
    }
}
