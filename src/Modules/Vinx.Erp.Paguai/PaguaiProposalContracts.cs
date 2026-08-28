using System.Text.Json.Serialization;

namespace Vinx.Erp.Paguai;

public sealed record PaguaiProposalSource(string Field, string Source);

public sealed record CreatePaguaiProposalCommand(
    Guid CompanyId,
    Guid RequestId,
    string Title,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement,
    Guid InvoiceFileId,
    double Confidence,
    IReadOnlyList<PaguaiProposalSource> SourceReferences,
    string PolicyVersion);

public sealed record ConfirmPaguaiProposalCommand(Guid CompanyId, Guid ProposalId, Guid RequestId);

public sealed record RejectPaguaiProposalCommand(Guid CompanyId, Guid ProposalId, Guid RequestId);

public sealed record PaguaiProposal(
    Guid Id,
    Guid CompanyId,
    string Title,
    string Description,
    [property: JsonConverter(typeof(PaguaiAmountJsonConverter))] decimal Amount,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement,
    Guid InvoiceFileId,
    double Confidence,
    IReadOnlyList<PaguaiProposalSource> SourceReferences,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public enum PaguaiProposalOperationState
{
    Created,
    Replay,
    Found,
    Invalid,
    Forbidden,
    NotFound,
    ProposalUnavailable,
    IdempotencyConflict,
    Unavailable,
}

public sealed record PaguaiProposalResult(
    PaguaiProposalOperationState State,
    PaguaiProposal? Proposal = null)
{
    public static PaguaiProposalResult Of(PaguaiProposalOperationState state) => new(state);
}

public sealed record PaguaiProposalConfirmationResult(
    PaguaiProposalOperationState State,
    PaguaiDraft? Draft = null)
{
    public static PaguaiProposalConfirmationResult Of(PaguaiProposalOperationState state) => new(state);
}

public sealed record PaguaiProposalMutationResult(PaguaiProposalOperationState State)
{
    public static PaguaiProposalMutationResult Of(PaguaiProposalOperationState state) => new(state);
}
