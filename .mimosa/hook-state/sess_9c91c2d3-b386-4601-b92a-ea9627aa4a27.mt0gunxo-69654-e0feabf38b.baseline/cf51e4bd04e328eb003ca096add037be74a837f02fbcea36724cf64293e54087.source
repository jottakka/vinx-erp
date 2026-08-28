using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Net.Http.Headers;

namespace Vinx.Erp.ApiHost.Identity;

internal sealed class BrowserBffNoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Antiforgery pode definir sua diretiva de cache ao emitir token.
        // Aplica antes dos headers saírem para manter a sessão browser não cacheável.
        // A decisão roda no OnStarting: a identidade da sessão cookie só está
        // disponível depois da autenticação, e respostas autenticadas por
        // BrowserSessionCookie nunca podem ser cacheadas, em nenhuma rota.
        context.Response.OnStarting(
            static state =>
            {
                var context = (HttpContext)state;
                var browserSessionRoute =
                    context.Request.Path.Equals("/foundation/bootstrap", StringComparison.Ordinal)
                    || context.Request.Path.StartsWithSegments("/foundation/identity/browser-session")
                    || context.Request.Path.StartsWithSegments("/paguai")
                    || context.Request.Path.StartsWithSegments("/atendimento");
                var endpointOptedIntoNoStore =
                    context.GetEndpoint()?.Metadata.GetMetadata<BrowserBffNoStoreEndpointMetadata>()
                    is not null;
                if (
                    browserSessionRoute
                    || endpointOptedIntoNoStore
                    || IsBrowserCookieAuthenticated(context)
                )
                {
                    context.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
                }

                return Task.CompletedTask;
            },
            context);

        return next(context);
    }

    private static bool IsBrowserCookieAuthenticated(HttpContext context) =>
        context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                BrowserBffAuthenticationExtensions.CookieScheme,
                StringComparison.Ordinal
            )
        );
}

internal sealed class BrowserBffAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!RequiresValidation(context))
        {
            await next(context);
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "invalid_request" });
            return;
        }

        if (ShouldClearBrowserSessionCookie(context))
        {
            context.Response.OnStarting(
                static state =>
                    ((HttpContext)state).SignOutAsync(
                        BrowserBffAuthenticationExtensions.CookieScheme
                    ),
                context
            );
        }

        await next(context);
    }

    private static bool ShouldClearBrowserSessionCookie(HttpContext context) =>
        HttpMethods.IsDelete(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<BrowserBffCookieOnlyEndpointMetadata>()
            is not null
        && context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                BrowserBffAuthenticationExtensions.CookieScheme,
                StringComparison.Ordinal
            )
        );

    private static bool RequiresValidation(HttpContext context)
    {
        if (
            !HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)
            && !HttpMethods.IsDelete(context.Request.Method)
        )
        {
            return false;
        }

        var antiforgeryRequirement = context
            .GetEndpoint()
            ?.Metadata.GetMetadata<BrowserBffAntiforgeryRequiredMetadata>();
        if (antiforgeryRequirement is { CookieAuthenticatedOnly: false })
        {
            return true;
        }

        return context.User.Identities.Any(identity =>
            string.Equals(
                identity.AuthenticationType,
                BrowserBffAuthenticationExtensions.CookieScheme,
                StringComparison.Ordinal
            )
        );
    }
}

internal sealed class BrowserBffNoStoreEndpointMetadata;
