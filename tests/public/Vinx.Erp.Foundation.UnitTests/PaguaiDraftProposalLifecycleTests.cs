using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class PaguaiDraftProposalLifecycleTests
{
    private static readonly Guid ProposalId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid RequestId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid FileId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly DateTimeOffset DatabaseNow = new(2026, 8, 26, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_keeps_only_typed_safe_fields_and_a_fifteen_minute_expiry()
    {
        var proposal = NewProposal();

        Assert.Equal(ProposalId, proposal.Id);
        Assert.Equal(CompanyId, proposal.CompanyId);
        Assert.Equal(ActorId, proposal.ActorUserId);
        Assert.Equal(RequestId, proposal.RequestId);
        Assert.Equal(FileId, proposal.FileRecordId);
        Assert.Equal("Office supplies", proposal.Title);
        Assert.Equal("Replacement paper", proposal.Description);
        Assert.Equal(250.00m, proposal.Amount);
        Assert.Equal("BRL", proposal.Currency);
        Assert.Equal(new DateOnly(2026, 9, 10), proposal.DueDate);
        Assert.Equal("Fornecedor Demo", proposal.BeneficiaryDisplayName);
        Assert.Equal("optional", proposal.ProofRequirement);
        Assert.Equal(0.91, proposal.Confidence);
        Assert.Equal("amount=55555555-5555-4555-8555-555555555555:p1", proposal.SourceReferences);
        Assert.Equal("paguai_from_document_v1", proposal.PolicyVersion);
        Assert.Equal(new string('a', 64), proposal.PayloadHash);
        Assert.Equal(PaguaiDraftProposal.PendingState, proposal.State);
        Assert.Equal(DatabaseNow, proposal.CreatedAt);
        Assert.Equal(DatabaseNow.AddMinutes(15), proposal.ExpiresAt);
        Assert.Null(proposal.ResultDraftId);
    }

    [Fact]
    public void Pending_proposal_can_be_consumed_exactly_once()
    {
        var proposal = NewProposal();
        var draftId = Guid.Parse("66666666-6666-4666-8666-666666666666");

        proposal.Consume(draftId, ActorId, RequestId, DatabaseNow.AddMinutes(1));

        Assert.Equal(PaguaiDraftProposal.ConsumedState, proposal.State);
        Assert.Equal(draftId, proposal.ResultDraftId);
        Assert.Equal(DatabaseNow.AddMinutes(1), proposal.ResolvedAt);
        Assert.Throws<InvalidOperationException>(() =>
            proposal.Consume(Guid.NewGuid(), ActorId, Guid.NewGuid(), DatabaseNow.AddMinutes(2)));
    }

    [Fact]
    public void Rejected_invalidated_and_expired_proposals_are_terminal()
    {
        var rejected = NewProposal();
        rejected.Reject(ActorId, RequestId, DatabaseNow.AddMinutes(1));
        Assert.Equal(PaguaiDraftProposal.RejectedState, rejected.State);
        Assert.Throws<InvalidOperationException>(() =>
            rejected.Consume(Guid.NewGuid(), ActorId, Guid.NewGuid(), DatabaseNow.AddMinutes(2)));

        var invalidated = NewProposal(Guid.NewGuid());
        invalidated.Invalidate(ActorId, RequestId, DatabaseNow.AddMinutes(1));
        Assert.Equal(PaguaiDraftProposal.InvalidatedState, invalidated.State);
        Assert.Throws<InvalidOperationException>(() =>
            invalidated.Reject(ActorId, Guid.NewGuid(), DatabaseNow.AddMinutes(2)));

        var expired = NewProposal(Guid.NewGuid());
        Assert.True(expired.ExpireIfDue(DatabaseNow.AddMinutes(15), ActorId, RequestId));
        Assert.Equal(PaguaiDraftProposal.InvalidatedState, expired.State);
        Assert.False(expired.ExpireIfDue(DatabaseNow.AddMinutes(16), ActorId, RequestId));
    }

    [Fact]
    public void Proposal_cannot_resolve_before_creation_or_after_expiry()
    {
        var proposal = NewProposal();

        Assert.Throws<InvalidOperationException>(() =>
            proposal.Reject(ActorId, RequestId, DatabaseNow.AddSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() =>
            proposal.Consume(Guid.NewGuid(), ActorId, RequestId, proposal.ExpiresAt));
    }

    private static PaguaiDraftProposal NewProposal(Guid? id = null) => PaguaiDraftProposal.Create(
        id ?? ProposalId,
        CompanyId,
        ActorId,
        RequestId,
        FileId,
        "Office supplies",
        "Replacement paper",
        250.00m,
        "BRL",
        new DateOnly(2026, 9, 10),
        "Fornecedor Demo",
        "optional",
        0.91,
        "amount=55555555-5555-4555-8555-555555555555:p1",
        "paguai_from_document_v1",
        new string('a', 64),
        DatabaseNow);
}
