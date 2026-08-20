namespace Vinx.Erp.Paguai;

public sealed record CreateDraftCommand(
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description,
    long AmountMinor,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement
);

public sealed record PaguaiDraft(
    Guid Id,
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description,
    long AmountMinor,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement,
    string State,
    int Version,
    DateTimeOffset CreatedAt
);

public enum PaguaiDraftState
{
    Created,
    Replay,
    Invalid,
    Forbidden,
    NotFound,
    IdempotencyConflict,
    Unavailable,
}

public sealed record PaguaiDraftResult(PaguaiDraftState State, PaguaiDraft? Draft = null)
{
    public static PaguaiDraftResult Of(PaguaiDraftState state) => new(state);
}

public sealed record PaguaiDraftListResult(
    PaguaiDraftState State,
    IReadOnlyList<PaguaiDraft>? Drafts = null
)
{
    public static PaguaiDraftListResult Of(PaguaiDraftState state) => new(state);
}
