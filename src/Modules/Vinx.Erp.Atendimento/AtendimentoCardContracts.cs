namespace Vinx.Erp.Atendimento;

public static class AtendimentoPriority
{
    public const string Critical = "critica";
    public const string High = "alta";
    public const string Medium = "media";
    public const string Low = "baixa";
}

public static class AtendimentoChannel
{
    public const string WhatsApp = "whatsapp";
    public const string Email = "email";
    public const string Web = "web";
    public const string Manual = "manual";
}

public sealed record CreateAtendimentoCardCommand(
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description
)
{
    public string? Priority { get; init; }

    public string? Channel { get; init; }

    public DateTimeOffset? SlaDueAt { get; init; }

    public Guid? AttachmentFileId { get; init; }

    public IReadOnlyList<Guid>? ParticipantMembershipIds { get; init; }
}

public sealed record AssignAtendimentoCardCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    Guid AssigneeMembershipId
);

public sealed record MoveAtendimentoCardCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    string Column
);

public sealed record CancelAtendimentoCardCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    string Reason
);

public sealed record AtendimentoCardResponse(
    Guid Id,
    Guid CompanyId,
    string Title,
    string Description,
    string Column,
    long Version,
    Guid? AssigneeMembershipId
)
{
    public string Priority { get; init; } = AtendimentoPriority.Medium;

    public string Channel { get; init; } = AtendimentoChannel.Manual;

    public DateTimeOffset? SlaDueAt { get; init; }

    public bool IsOverdue { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public Guid? AttachmentFileId { get; init; }

    public IReadOnlyList<Guid> ParticipantMembershipIds { get; init; } = [];
}

public sealed record AtendimentoAssignmentOptionResponse(Guid MembershipId, string DisplayName);

public enum AtendimentoCardOperationState
{
    Created,
    Replay,
    Success,
    Invalid,
    MembershipRequired,
    CapabilityDenied,
    ResourceNotFound,
    IdempotencyConflict,
    IntegrityConflict,
    TerminalState,
    Unavailable,
}

public sealed record AtendimentoCardOperationResult(
    AtendimentoCardOperationState State,
    AtendimentoCardResponse? Card = null,
    AtendimentoCardResponse? Current = null,
    IReadOnlyList<AtendimentoCardResponse>? Cards = null,
    IReadOnlyList<AtendimentoAssignmentOptionResponse>? AssignmentOptions = null
);
