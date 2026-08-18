using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Identity;

public enum InternalSessionAuthenticationState
{
    Authenticated,
    Unauthenticated,
    Unavailable,
}

public sealed record InternalSessionAuthenticationResult(
    InternalSessionAuthenticationState State,
    ActorContext? Actor = null)
{
    public static InternalSessionAuthenticationResult Authenticated(ActorContext actor) =>
        new(InternalSessionAuthenticationState.Authenticated, actor);

    public static InternalSessionAuthenticationResult Unauthenticated() =>
        new(InternalSessionAuthenticationState.Unauthenticated);

    public static InternalSessionAuthenticationResult Unavailable() =>
        new(InternalSessionAuthenticationState.Unavailable);
}

/// <summary>
/// Resolve e admite uma identidade autenticada antes de iniciar uma sessão do browser.
/// </summary>
public sealed class InternalSessionAuthenticator(
    IIdentityProvider identityProvider,
    InternalIdentityResolver identityResolver,
    InternalSessionAdmissionService admissionService,
    ILogger<InternalSessionAuthenticator> logger)
{
    private const string Operation = "identity_gate";
    private const string Module = "identity";
    private const string UnavailableReason = "database_resolution_unavailable";
    private const string EventTemplate = "Identity gate {Operation} {Module} {Result} {Reason}";

    public async Task<InternalSessionAuthenticationResult> AuthenticateAsync(
        ClaimsPrincipal principal,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (
            principal.Identity?.IsAuthenticated != true
            || !identityProvider.TryGetExternalIdentityProof(principal, out var proof)
        )
        {
            return InternalSessionAuthenticationResult.Unauthenticated();
        }

        ResolvedInternalIdentity? identity;
        try
        {
            identity = await identityResolver.ResolveAsync(proof.Key, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                UnavailableReason
            );
            return InternalSessionAuthenticationResult.Unavailable();
        }

        if (identity is null)
        {
            return InternalSessionAuthenticationResult.Unauthenticated();
        }

        var admission = await admissionService.AdmitAsync(
            identity.UserId,
            proof,
            AuditSource.Web,
            correlationId,
            cancellationToken);
        return admission.State switch
        {
            SessionAdmissionState.Admitted or SessionAdmissionState.Active =>
                InternalSessionAuthenticationResult.Authenticated(
                    new ActorContext(
                        identity.UserId,
                        correlationId,
                        identity.ExternalIdentityId,
                        proof.AssuranceLevel)),
            SessionAdmissionState.Denied => InternalSessionAuthenticationResult.Unauthenticated(),
            _ => InternalSessionAuthenticationResult.Unavailable(),
        };
    }
}
