using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Foundation.Platform;

public static class PlatformOperatorAuthorization
{
    public const string PolicyName = "platform.operator";

    private static readonly object AccessStateKey = new();

    internal static void SetAccessState(HttpContext context, PlatformOperatorAccessState state) =>
        context.Items[AccessStateKey] = state;

    internal static PlatformOperatorAccessState? GetAccessState(HttpContext context) =>
        context.Items.TryGetValue(AccessStateKey, out var value)
        && value is PlatformOperatorAccessState state
            ? state
            : null;
}

public sealed class PlatformOperatorRequirement : IAuthorizationRequirement { }

public sealed record PlatformOperatorBootstrapResponse(bool IsPlatformOperator);

public sealed class PlatformOperatorAuthorizationHandler(
    PlatformOperatorAccessService accessService,
    ILogger<PlatformOperatorAuthorizationHandler> logger
) : AuthorizationHandler<PlatformOperatorRequirement>
{
    private const string Operation = "platform_operator_authorization";
    private const string Module = "foundation";
    private const string EventTemplate =
        "Platform operator policy {Operation} {Module} {Result} {Reason}";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformOperatorRequirement requirement
    )
    {
        if (context.Resource is not HttpContext httpContext)
        {
            logger.LogError(
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                "http_context_missing"
            );
            return;
        }

        ActorContext actor;
        try
        {
            actor = httpContext.GetRequiredActorContext();
        }
        catch (InvalidOperationException)
        {
            PlatformOperatorAuthorization.SetAccessState(
                httpContext,
                PlatformOperatorAccessState.Unavailable
            );
            logger.LogError(
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                "actor_context_missing"
            );
            return;
        }

        var access = await accessService.RequireAsync(actor, httpContext.RequestAborted);
        PlatformOperatorAuthorization.SetAccessState(httpContext, access.State);
        if (access.State is PlatformOperatorAccessState.Allowed)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class PlatformOperatorAuthorizationResultHandler
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    )
    {
        if (
            authorizeResult.Forbidden
            && policy.Requirements.OfType<PlatformOperatorRequirement>().Any()
        )
        {
            var code =
                PlatformOperatorAuthorization.GetAccessState(context)
                is PlatformOperatorAccessState.Unavailable
                    ? "platform_operator_unavailable"
                    : "platform_operator_required";
            var statusCode = code is "platform_operator_unavailable"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status403Forbidden;
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsJsonAsync(new { code });
        }

        return _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
