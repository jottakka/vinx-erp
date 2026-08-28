using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Vinx.Erp.Foundation.Authorization;

namespace Vinx.Erp.Foundation.Identity;

public sealed class InternalSessionGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        InternalSessionAuthenticator authenticator
    )
    {
        var endpoint = context.GetEndpoint();
        if (
            endpoint is null
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
        )
        {
            await next(context);
            return;
        }

        var authentication = await authenticator.AuthenticateAsync(
            context.User,
            ServerCorrelationContext.Get(context).CorrelationId,
            context.RequestAborted);
        if (authentication is
            {
                State: InternalSessionAuthenticationState.Authenticated,
                Actor: { } actor,
            })
        {
            context.SetActorContext(actor);
            await next(context);
            return;
        }

        if (authentication.State == InternalSessionAuthenticationState.Unauthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await WriteUnavailableAsync(context);
    }

    private static async Task WriteUnavailableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new AuthorizationErrorResponse("identity_gate_unavailable")
        );
    }
}
