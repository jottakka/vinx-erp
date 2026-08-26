namespace Vinx.Erp.Foundation.Domain;

public enum AgentExecutionLedgerState
{
    Claimed,
    VisionInFlight,
    ModelInFlight,
    Completed,
    FailedClosed,
    Ambiguous
}

/// <summary>
/// Registro durável e monotônico de uma execução do agente. Ele guarda somente
/// correlação opaca, estado e resultado seguro; nunca prompt, conteúdo ou endereço.
/// </summary>
public sealed class AgentExecutionLedger : ICompanyScoped, IConcurrencyVersioned
{
    private AgentExecutionLedger()
    {
    }

    private AgentExecutionLedger(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string channel,
        Guid? inboundMessageId,
        Guid requestId,
        string journey,
        string payloadHash)
    {
        ValidateIdentifiers(id, companyId, actorUserId, requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(journey);
        ValidateChannel(channel, inboundMessageId);
        if (!IsSha256(payloadHash))
        {
            throw new ArgumentException("The payload hash must be a lowercase SHA-256 hexadecimal value.", nameof(payloadHash));
        }

        Id = id;
        CompanyId = companyId;
        ActorUserId = actorUserId;
        Channel = channel;
        InboundMessageId = inboundMessageId;
        RequestId = requestId;
        Journey = journey;
        PayloadHash = payloadHash;
        State = AgentExecutionLedgerState.Claimed;
        RowVersion = 1;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string Channel { get; private set; } = null!;
    public Guid? InboundMessageId { get; private set; }
    public Guid RequestId { get; private set; }
    public string Journey { get; private set; } = null!;
    public string PayloadHash { get; private set; } = null!;
    public AgentExecutionLedgerState State { get; private set; }
    public string? SafeResultJson { get; private set; }
    public string? ReasonCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long RowVersion { get; private set; }

    public Company Company { get; private set; } = null!;

    public static AgentExecutionLedger Create(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string channel,
        Guid? inboundMessageId,
        Guid requestId,
        string journey,
        string payloadHash) => new(
        id,
        companyId,
        actorUserId,
        channel,
        inboundMessageId,
        requestId,
        journey,
        payloadHash);

    public void MarkVisionInFlight(Guid actorUserId, Guid requestId) =>
        Transition(AgentExecutionLedgerState.Claimed, AgentExecutionLedgerState.VisionInFlight, actorUserId, requestId);

    public void MarkModelInFlight(Guid actorUserId, Guid requestId) =>
        Transition(
            [AgentExecutionLedgerState.Claimed, AgentExecutionLedgerState.VisionInFlight],
            AgentExecutionLedgerState.ModelInFlight,
            actorUserId,
            requestId);

    public void Complete(string safeResultJson, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeResultJson);
        Transition(AgentExecutionLedgerState.ModelInFlight, AgentExecutionLedgerState.Completed, actorUserId, requestId);
        SafeResultJson = safeResultJson;
    }

    public void FailClosed(string reasonCode, Guid actorUserId, Guid requestId) =>
        TransitionToTerminal(
            [AgentExecutionLedgerState.Claimed],
            AgentExecutionLedgerState.FailedClosed,
            reasonCode,
            actorUserId,
            requestId);

    public void MarkAmbiguous(string reasonCode, Guid actorUserId, Guid requestId) =>
        TransitionToTerminal(
            [AgentExecutionLedgerState.VisionInFlight, AgentExecutionLedgerState.ModelInFlight],
            AgentExecutionLedgerState.Ambiguous,
            reasonCode,
            actorUserId,
            requestId);

    private void TransitionToTerminal(
        AgentExecutionLedgerState[] expected,
        AgentExecutionLedgerState target,
        string reasonCode,
        Guid actorUserId,
        Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        Transition(expected, target, actorUserId, requestId);
        ReasonCode = reasonCode;
    }

    private void Transition(
        AgentExecutionLedgerState expected,
        AgentExecutionLedgerState target,
        Guid actorUserId,
        Guid requestId)
        => Transition([expected], target, actorUserId, requestId);

    private void Transition(
        AgentExecutionLedgerState[] expected,
        AgentExecutionLedgerState target,
        Guid actorUserId,
        Guid requestId)
    {
        VerifyActorAndRequest(actorUserId, requestId);
        if (!expected.Contains(State))
        {
            throw new InvalidOperationException($"The agent execution cannot move from {State} to {target}.");
        }

        State = target;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void VerifyActorAndRequest(Guid actorUserId, Guid requestId)
    {
        if (actorUserId != ActorUserId || requestId != RequestId)
        {
            throw new InvalidOperationException("The lifecycle mutation does not match the execution key.");
        }
    }

    private static void ValidateIdentifiers(Guid id, Guid companyId, Guid actorUserId, Guid requestId)
    {
        if (id == Guid.Empty || companyId == Guid.Empty || actorUserId == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Agent execution ledger identifiers must not be empty.", nameof(id));
        }
    }

    private static void ValidateChannel(string channel, Guid? inboundMessageId)
    {
        var validShape = channel switch
        {
            "web" => inboundMessageId is null,
            "whatsapp" or "telegram" => inboundMessageId is not null,
            _ => false
        };
        if (!validShape)
        {
            throw new ArgumentException("The channel and inbound message identifier are not a supported execution source.", nameof(channel));
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            (character is >= '0' and <= '9') || (character is >= 'a' and <= 'f'));
}
