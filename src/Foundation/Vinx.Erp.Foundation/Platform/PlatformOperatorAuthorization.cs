using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Foundation.Platform;

public static class PlatformOperatorAuthorization
{
    public const string PolicyName = "platform.operator";
    public const string MfaAssurancePolicyName = "platform.operator.mfa_assurance";

    private static readonly object AccessStateKey = new();

    internal static void SetAccessState(HttpContext context, PlatformOperatorAccessState state) =>
        context.Items[AccessStateKey] = state;

    internal static PlatformOperatorAccessState? GetAccessState(HttpContext context) =>
        context.Items.TryGetValue(AccessStateKey, out var value)
        && value is PlatformOperatorAccessState state
            ? state
            : null;
}

public sealed class PlatformOperatorRequirement(bool requiresMfaAssurance = false)
    : IAuthorizationRequirement
{
    public bool RequiresMfaAssurance { get; } = requiresMfaAssurance;
}

public sealed record PlatformOperatorBootstrapResponse(bool IsPlatformOperator);

public sealed class PlatformOperatorAuthorizationHandler(
    PlatformOperatorAccessService accessService,
    IMfaAssuranceVerifier mfaAssuranceVerifier,
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
        if (access.State is PlatformOperatorAccessState.Allowed && requirement.RequiresMfaAssurance)
        {
            access = await accessService.RequireMfaAssuranceAsync(actor, httpContext.RequestAborted);
            if (access.State is PlatformOperatorAccessState.Allowed)
            {
                access = await RequireReverifiedMfaAssuranceAsync(httpContext);
            }
        }

        PlatformOperatorAuthorization.SetAccessState(httpContext, access.State);
        if (access.State is PlatformOperatorAccessState.Allowed)
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// Rederiva o AAL2 no uso do privilégio: o claim "aal" do cookie de sessão é
    /// copiado no sign-in e não acompanha remoção/demotação do fator no IdP.
    /// O claim serve apenas como primeiro gate rápido (cookie aal1 nega sem chamar
    /// o provedor — RequireMfaAssuranceAsync já retorna antes deste passo); um
    /// cookie aal2 exige confirmação corrente de fator verificado na Admin API.
    /// Camadas permanecem intencionais: o GUC app.mfa_assurance aplicado por
    /// FoundationRlsContext/ApplyActorAsync continua refletindo o claim da sessão
    /// e não muda; como todo endpoint com gate SQL de MFA está atrás desta policy,
    /// a negação aqui falha fechado antes de qualquer fluxo que aplique o GUC.
    /// </summary>
    private async Task<PlatformOperatorAccessResult> RequireReverifiedMfaAssuranceAsync(
        HttpContext httpContext
    )
    {
        var subjectClaims = httpContext.User.FindAll("sub").ToArray();
        if (subjectClaims.Length != 1 || string.IsNullOrWhiteSpace(subjectClaims[0].Value))
        {
            logger.LogWarning(EventTemplate, Operation, Module, "denied", "subject_missing");
            return new PlatformOperatorAccessResult(PlatformOperatorAccessState.MfaAssuranceRequired);
        }

        var verification = await mfaAssuranceVerifier.VerifyAsync(
            subjectClaims[0].Value,
            httpContext.RequestAborted
        );
        if (verification.State is MfaAssuranceVerificationState.VerifiedFactorPresent)
        {
            return new PlatformOperatorAccessResult(PlatformOperatorAccessState.Allowed);
        }

        if (verification.State is MfaAssuranceVerificationState.VerifiedFactorAbsent)
        {
            // Negação de segurança relevante: o IdP não confirma mais fator verificado.
            // A indisponibilidade já foi registrada com o motivo pelo verificador.
            logger.LogWarning(
                EventTemplate,
                Operation,
                Module,
                "denied",
                "mfa_factor_not_verified"
            );
        }

        return new PlatformOperatorAccessResult(PlatformOperatorAccessState.MfaAssuranceRequired);
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
            var code = PlatformOperatorAuthorization.GetAccessState(context) switch
            {
                PlatformOperatorAccessState.MfaAssuranceRequired => "mfa_assurance_required",
                PlatformOperatorAccessState.Unavailable => "platform_operator_unavailable",
                _ => "platform_operator_required",
            };
            var statusCode = code is "platform_operator_unavailable"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status403Forbidden;
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsJsonAsync(new { code });
        }

        return _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
