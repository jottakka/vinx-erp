using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>Comando canônico do agente, independente do canal de entrada.</summary>
public sealed record AgentCommand(
    ActorContext Actor,
    string Channel,
    Guid? InboundMessageId,
    Guid RequestId,
    string Journey,
    string Message,
    Guid? CompanyId,
    IReadOnlyList<Guid>? Attachments = null)
{
    private static readonly IReadOnlyList<Guid> NoAttachments = Array.AsReadOnly(Array.Empty<Guid>());

    public IReadOnlyList<Guid> Attachments { get; init; } =
        Attachments is { Count: > 0 } attachments
            ? Array.AsReadOnly(attachments.ToArray())
            : NoAttachments;
}
