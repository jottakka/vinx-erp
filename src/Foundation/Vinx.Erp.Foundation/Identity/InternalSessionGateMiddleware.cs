using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Identity;

public sealed class InternalSessionGateMiddleware(
    RequestDelegate next,
    ILogger<InternalSessionGateMiddleware> logger)
{
    private const string Operation = "identity_gate";
    private const string Module = "identity";
    private const string UnavailableReason = "database_resolution_unavailable";
    private const string EventTemplate = "Identity gate {Operation} {Module} {Result} {Reason}";

    public async Task InvokeAsync(
        HttpContext context,
        IIdentityProvider identityProvider,
        InternalIdentityResolver identityResolver,
        InternalSessionAdmissionService admissionService)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true
            || !identityProvider.TryGetExternalIdentityProof(context.User, out var proof))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Guid? userId;
        try
        {
            userId = await identityResolver.ResolveAsync(proof.Key, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, EventTemplate, Operation, Module, "unavailable", UnavailableReason);
            await WriteUnavailableAsync(context);
            return;
        }

        if (userId is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var admission = await admissionService.AdmitAsync(
            userId.Value,
            proof,
            AuditSource.Web,
            ServerCorrelationContext.Get(context).CorrelationId,
            context.RequestAborted);
        if (admission.State is SessionAdmissionState.Admitted or SessionAdmissionState.Active)
        {
            context.SetActorContext(new ActorContext(
                userId.Value,
                ServerCorrelationContext.Get(context).CorrelationId));
            await next(context);
            return;
        }

        if (admission.State == SessionAdmissionState.Denied)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await WriteUnavailableAsync(context);
    }

    private static async Task WriteUnavailableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new AuthorizationErrorResponse("identity_gate_unavailable"));
    }
}
