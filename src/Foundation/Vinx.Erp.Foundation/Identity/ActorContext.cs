using Microsoft.AspNetCore.Http;
using Vinx.Erp.Foundation.Observability;

namespace Vinx.Erp.Foundation.Identity;

/// <summary>
/// Contexto de ator resolvido pelo servidor; políticas globais usam a identidade externa exata quando disponível.
/// </summary>
public sealed record ActorContext(Guid UserId, Guid CorrelationId, Guid? ExternalIdentityId = null);

public static class ServerCorrelationContext
{
    private static readonly object ItemKey = new();

    public static CorrelationContext Begin(HttpContext context)
    {
        var correlation = new CorrelationContext(Guid.NewGuid());
        context.Items.Add(ItemKey, correlation);
        return correlation;
    }

    public static CorrelationContext Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is CorrelationContext correlation
            ? correlation
            : throw new InvalidOperationException(
                "The request correlation context was not initialized."
            );
}
