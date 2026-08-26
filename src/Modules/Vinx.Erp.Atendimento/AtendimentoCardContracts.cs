namespace Vinx.Erp.Atendimento;

public sealed record CreateAtendimentoCardCommand(
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description
);

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
);

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

public sealed record AtendimentoCardResponse(
    Guid Id,
    Guid CompanyId,
    string Title,
    string Description,
    string Column,
    long Version,
    Guid? AssigneeMembershipId,
    string? Priority,
    Guid? CategoryRefId,
    Guid? ChannelRefId,
    DateTimeOffset? SlaDueAt,
    bool Overdue,
    string? RequesterPersonRef,
    IReadOnlyList<Guid>? WatcherMembershipIds
);

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
    Unavailable,
}

public sealed record AtendimentoCardOperationResult(
    AtendimentoCardOperationState State,
    AtendimentoCardResponse? Card = null,
    AtendimentoCardResponse? Current = null,
    IReadOnlyList<AtendimentoCardResponse>? Cards = null
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
    Replay,
    Conflict,
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
