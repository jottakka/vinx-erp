using Microsoft.AspNetCore.Http;

namespace Vinx.Erp.Foundation.Identity;

public static class ActorContextHttpContextExtensions
{
    private static readonly object ActorContextKey = new();

    public static void SetActorContext(this HttpContext context, ActorContext actor) =>
        context.Items[ActorContextKey] = actor;

    public static ActorContext GetRequiredActorContext(this HttpContext context) =>
        context.Items.TryGetValue(ActorContextKey, out var value) && value is ActorContext actor
            ? actor
            : throw new InvalidOperationException("The identity gate did not establish an actor.");
}
