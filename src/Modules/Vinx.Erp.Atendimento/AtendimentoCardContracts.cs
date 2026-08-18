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

public sealed record AtendimentoCardResponse(
    Guid Id,
    Guid CompanyId,
    string Title,
    string Description,
    string Column,
    long Version,
    Guid? AssigneeMembershipId
);

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
    Unavailable,
}

public sealed record AtendimentoCardOperationResult(
    AtendimentoCardOperationState State,
    AtendimentoCardResponse? Card = null,
    AtendimentoCardResponse? Current = null,
    IReadOnlyList<AtendimentoCardResponse>? Cards = null
);
