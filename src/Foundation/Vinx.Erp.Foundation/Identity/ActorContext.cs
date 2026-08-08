using System.Security.Claims;

namespace Vinx.Erp.Foundation.Identity;

public sealed record ActorContext(Guid UserId, Guid CorrelationId)
{
    public static ActorContext FromAuthenticatedPrincipal(ClaimsPrincipal principal)
    {
        return TryFromAuthenticatedPrincipal(principal, out var actor)
            ? actor
            : throw new InvalidOperationException("The authenticated principal is missing required identity context.");
    }

    public static bool TryFromAuthenticatedPrincipal(ClaimsPrincipal? principal, out ActorContext actor)
    {
        actor = null!;
        if (principal?.Identity?.IsAuthenticated != true
            || !TryParseClaim(principal, ClaimTypes.NameIdentifier, out var userId)
            || !TryParseClaim(principal, "correlation_id", out var correlationId))
        {
            return false;
        }

        actor = new ActorContext(userId, correlationId);
        return true;
    }

    private static bool TryParseClaim(ClaimsPrincipal principal, string claimType, out Guid value) =>
        Guid.TryParse(principal.FindFirst(claimType)?.Value, out value);
}
