using System.ComponentModel.DataAnnotations;

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

/// <summary>
/// Campos de ticket opcionais com semântica nullable-means-unchanged;
/// menções geram intents de notificação determinísticas pós-commit.
/// </summary>
public sealed record UpdateAtendimentoCardFieldsCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    string? Priority,
    Guid? CategoryRefId,
    Guid? ChannelRefId,
    DateTimeOffset? SlaDueAt,
    string? RequesterPersonRef,
    IReadOnlyList<Guid>? MentionedMembershipIds
)
{
    /// <summary>Teto de menções por comando (espelha MaxParticipants da criação).</summary>
    public const int MaxMentionedMembershipIds = 25;

    /// <summary>Teto da coluna requester_person_ref (varchar(200)).</summary>
    public const int MaxRequesterPersonRefLength = 200;
}

public sealed record AtendimentoCardWatcherCommand(
    Guid CompanyId,
    Guid RequestId,
    long ExpectedVersion,
    Guid MemberMembershipId
);

public sealed record CreateAtendimentoCategoryCommand(
    Guid CompanyId,
    Guid RequestId,
    string Name
);

public sealed record DeactivateAtendimentoCategoryCommand(
    Guid CompanyId,
    Guid RequestId,
    Guid CategoryId
);

public sealed record AtendimentoCardQueueFilter(
    string? Status,
    string? Priority,
    Guid? AssigneeMembershipId,
    bool Overdue,
    string? Search
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
    [AllowedValues(AtendimentoPriority.Critical, AtendimentoPriority.High, AtendimentoPriority.Medium, AtendimentoPriority.Low)]
    public string? Priority { get; init; }

    [AllowedValues(AtendimentoChannel.WhatsApp, AtendimentoChannel.Email, AtendimentoChannel.Web, AtendimentoChannel.Manual)]
    public string Channel { get; init; } = AtendimentoChannel.Manual;

    public DateTimeOffset? SlaDueAt { get; init; }

    public bool IsOverdue { get; init; }

    public Guid? CategoryRefId { get; init; }

    public Guid? ChannelRefId { get; init; }

    public string? RequesterPersonRef { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public Guid? AttachmentFileId { get; init; }

    public IReadOnlyList<Guid> ParticipantMembershipIds { get; init; } = [];

    public IReadOnlyList<Guid>? WatcherMembershipIds { get; init; }
}

public sealed record AtendimentoAssignmentOptionResponse(Guid MembershipId, string DisplayName);

public enum AtendimentoCardOperationState
{
    Created,
    Replay,
    NoChange,
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

public sealed record AtendimentoLookupCategoryItem(Guid Id, string Name, bool IsActive);

public sealed record AtendimentoLookupChannelItem(Guid Id, string Key, bool IsActive);

public sealed record AtendimentoLookupsResponse(
    IReadOnlyList<AtendimentoLookupCategoryItem> Categories,
    IReadOnlyList<AtendimentoLookupChannelItem> Channels
);

public enum AtendimentoLookupsReadState
{
    Success,
    Invalid,
    MembershipRequired,
    CapabilityDenied,
    Unavailable,
}

public sealed record AtendimentoLookupsResult(
    AtendimentoLookupsReadState State,
    AtendimentoLookupsResponse? Lookups = null
);

public sealed record AtendimentoCategoryResponse(Guid Id, Guid CompanyId, string Name);

public enum AtendimentoLookupWriteState
{
    Created,
    Success,
    Replay,
    Conflict,
    IdempotencyConflict,
    Invalid,
    MembershipRequired,
    CapabilityDenied,
    ResourceNotFound,
    Unavailable,
}

public sealed record AtendimentoLookupWriteResult(
    AtendimentoLookupWriteState State,
    AtendimentoCategoryResponse? Category = null
);

public sealed record AtendimentoTimelineEntry(
    Guid Id,
    long EventSeq,
    string ActionKind,
    Guid? ActorMembershipId,
    string ActorDisplayName,
    DateTimeOffset OccurredAt
);

public enum AtendimentoTimelineReadState
{
    Success,
    Invalid,
    MembershipRequired,
    CapabilityDenied,
    ResourceNotFound,
    Unavailable,
}

public sealed record AtendimentoTimelineResult(
    AtendimentoTimelineReadState State,
    IReadOnlyList<AtendimentoTimelineEntry>? Entries = null
);
