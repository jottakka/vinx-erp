using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Net.Http.Headers;

namespace Vinx.Erp.ApiHost.Identity;

internal sealed class BrowserBffNoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/foundation/bootstrap", StringComparison.Ordinal)
            || context.Request.Path.StartsWithSegments("/foundation/identity/browser-session"))
        {
            // Antiforgery pode definir sua diretiva de cache ao emitir token.
            // Aplica antes dos headers saírem para manter a sessão browser não cacheável.
            context.Response.OnStarting(
                static state =>
                {
                    var response = (HttpResponse)state;
                    response.Headers[HeaderNames.CacheControl] = "private, no-store";
                    return Task.CompletedTask;
                },
                context.Response);
        }

        return next(context);
    }
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
                static state => ((HttpContext)state).SignOutAsync(
                    BrowserBffAuthenticationExtensions.CookieScheme),
                context);
        }

        await next(context);
    }

    private static bool ShouldClearBrowserSessionCookie(HttpContext context) =>
        HttpMethods.IsDelete(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<BrowserBffCookieOnlyEndpointMetadata>() is not null
        && context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                BrowserBffAuthenticationExtensions.CookieScheme,
                StringComparison.Ordinal));

    private static bool RequiresValidation(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)
            && !HttpMethods.IsDelete(context.Request.Method))
        {
            return false;
        }

        return context.GetEndpoint()?.Metadata.GetMetadata<BrowserBffAntiforgeryRequiredMetadata>() is not null
            || context.User.Identities.Any(identity =>
                string.Equals(identity.AuthenticationType, BrowserBffAuthenticationExtensions.CookieScheme, StringComparison.Ordinal));
    }
}
