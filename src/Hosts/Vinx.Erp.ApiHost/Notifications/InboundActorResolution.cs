namespace Vinx.Erp.ApiHost.Notifications;

public enum InboundActorResolutionState
{
    Admitted,
    Unbound,
    Ambiguous,
    Unverified,
    Inactive,
}

/// <summary>
/// Resultado fechado da resolução de identidade inbound: somente
/// <see cref="InboundActorResolutionState.Admitted" /> carrega usuário,
/// empresa e membership; os demais estados nunca escolhem tenant e nunca
/// deixam o resultado em aberto.
/// </summary>
public sealed record InboundActorResolution
{
    private InboundActorResolution(
        InboundActorResolutionState state,
        Guid? userId,
        Guid? companyId,
        Guid? membershipId
    )
    {
        State = state;
        UserId = userId;
        CompanyId = companyId;
        MembershipId = membershipId;
    }

    public InboundActorResolutionState State { get; }

    public Guid? UserId { get; }

    public Guid? CompanyId { get; }

    public Guid? MembershipId { get; }

    public bool IsAdmitted =>
        State == InboundActorResolutionState.Admitted
        && UserId.HasValue
        && CompanyId.HasValue
        && MembershipId.HasValue;

    public static InboundActorResolution Admitted(
        Guid userId,
        Guid companyId,
        Guid membershipId
    ) => new(InboundActorResolutionState.Admitted, userId, companyId, membershipId);

    public static InboundActorResolution Closed(InboundActorResolutionState state)
    {
        if (state == InboundActorResolutionState.Admitted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Admitted requires the resolved identity; use InboundActorResolution.Admitted."
            );
        }

        return new InboundActorResolution(state, null, null, null);
    }

    internal string SerializeState() =>
        State switch
        {
            InboundActorResolutionState.Admitted => "admitted",
            InboundActorResolutionState.Unbound => "unbound",
            InboundActorResolutionState.Ambiguous => "ambiguous",
            InboundActorResolutionState.Unverified => "unverified",
            InboundActorResolutionState.Inactive => "inactive",
            _ => throw new InvalidOperationException(
                "Inbound whatsapp actor resolution state is not serializable."
            ),
        };
}
