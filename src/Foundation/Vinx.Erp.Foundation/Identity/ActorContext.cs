using System.Security.Claims;

namespace Vinx.Erp.Foundation.Identity;

public sealed record ActorContext(Guid UserId, Guid CorrelationId)
{
    public static ActorContext FromAuthenticatedPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var userId = ParseRequiredClaim(principal, ClaimTypes.NameIdentifier);
        var correlationId = ParseRequiredClaim(principal, "correlation_id");
        return new ActorContext(userId, correlationId);
    }

    private static Guid ParseRequiredClaim(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirst(claimType)?.Value;
        if (!Guid.TryParse(value, out var parsedValue))
        {
            throw new InvalidOperationException($"The authenticated principal is missing a valid {claimType} claim.");
        }

        return parsedValue;
    }
}
