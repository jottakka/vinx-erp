using System.Text.Json;
using Vinx.Erp.Foundation.Approvals;
using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class ApprovalContractsTests
{
    [Fact]
    public void Canonical_hash_is_independent_of_key_order_and_nesting_style()
    {
        var first = CanonicalPayloadHasher.ComputeHash(Parse("""
            {"b":2,"a":1,"n":{"z":true,"y":[1,2,3]},"texto":"ação"}
            """));
        var second = CanonicalPayloadHasher.ComputeHash(Parse("""
            {"a":1,"texto":"ação","n":{"y":[1,2,3],"z":true},"b":2}
            """));

        Assert.Equal(first, second);
        Assert.Equal(CanonicalPayloadHasher.HashLength, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Canonical_hash_changes_with_payload_content()
    {
        var original = CanonicalPayloadHasher.ComputeHash(Parse("""{"valor":1}"""));
        var changed = CanonicalPayloadHasher.ComputeHash(Parse("""{"valor":2}"""));

        Assert.NotEqual(original, changed);
    }

    [Fact]
    public void Canonical_form_preserves_number_representation_without_culture_influence()
    {
        var canonical = CanonicalPayloadHasher.Canonicalize(Parse("""{"b":null,"a":1.25}"""));

        Assert.Equal("""{"a":1.25,"b":null}""", canonical);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567", false)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678", false)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    public void IsValidHash_enforces_sha256_lowercase_hexadecimal_format(string? hash, bool expected) =>
        Assert.Equal(expected, CanonicalPayloadHasher.IsValidHash(hash));

    [Fact]
    public void Prepare_command_validates_identifiers_payload_and_policy_version()
    {
        var companyId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var payload = Parse("""{"alvo":"estoque"}""");

        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            Guid.Empty, requestId, "ai.writes.record", payload, "2026-08-14.1"));
        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            companyId, Guid.Empty, "ai.writes.record", payload, "2026-08-14.1"));
        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            companyId, requestId, " ", payload, "2026-08-14.1"));
        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            companyId, requestId, "ai.writes.record", Parse("[1]"), "2026-08-14.1"));
        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            companyId, requestId, "ai.writes.record", payload, " "));
        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            companyId, requestId, "ai.writes.record", payload, "2026-08-14.1", TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new PrepareApprovalCommand(
            companyId,
            requestId,
            "ai.writes.record",
            payload,
            "2026-08-14.1",
            PrepareApprovalCommand.MaximumTimeToLive + TimeSpan.FromSeconds(1)));

        var command = new PrepareApprovalCommand(companyId, requestId, "ai.writes.record", payload, "2026-08-14.1");
        Assert.Equal(PrepareApprovalCommand.DefaultTimeToLive, command.TimeToLive);
        Assert.Equal("2026-08-14.1", command.PolicyVersion);
    }

    [Fact]
    public void Confirm_and_veto_commands_validate_their_identifiers()
    {
        var companyId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new ConfirmApprovalCommand(
            Guid.Empty, approvalId, proposalId, requestId, Parse("{}"), "v"));
        Assert.Throws<ArgumentException>(() => new ConfirmApprovalCommand(
            companyId, approvalId, proposalId, requestId, Parse("{}"), " "));
        Assert.Throws<ArgumentException>(() => new ConfirmApprovalCommand(
            companyId, approvalId, proposalId, requestId, Parse("1"), "v"));
        Assert.Throws<ArgumentException>(() => new VetoApprovalCommand(
            Guid.Empty, approvalId, proposalId, requestId));

        var confirm = new ConfirmApprovalCommand(companyId, approvalId, proposalId, requestId, Parse("{}"), "v");
        Assert.Equal(proposalId, confirm.ProposalId);
        var veto = new VetoApprovalCommand(companyId, approvalId, proposalId, requestId);
        Assert.Equal(requestId, veto.RequestId);
    }

    [Fact]
    public void Approval_lifecycle_transitions_are_terminal_and_single_use()
    {
        var actorUserId = Guid.NewGuid();
        var approval = new FoundationApproval(
            Guid.NewGuid(),
            Guid.NewGuid(),
            actorUserId,
            "ai.writes.record",
            new string('a', 64),
            "2026-08-14.1",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            Guid.NewGuid());

        Assert.False(approval.IsConsumed);
        Assert.False(approval.IsVetoed);
        Assert.Throws<ArgumentException>(() => approval.MarkConsumed(Guid.Empty, "{}", actorUserId));

        approval.MarkConsumed(Guid.NewGuid(), """{"outcome":"claimed"}""", actorUserId);

        Assert.True(approval.IsConsumed);
        Assert.NotNull(approval.ConfirmationRequestId);
        Assert.Throws<InvalidOperationException>(() => approval.MarkConsumed(Guid.NewGuid(), "{}", actorUserId));
        Assert.Throws<InvalidOperationException>(() => approval.Veto(actorUserId, Guid.NewGuid()));
    }

    [Fact]
    public void A_vetoed_approval_is_idempotent_but_never_consumable()
    {
        var actorUserId = Guid.NewGuid();
        var approval = new FoundationApproval(
            Guid.NewGuid(),
            Guid.NewGuid(),
            actorUserId,
            "ai.writes.record",
            new string('b', 64),
            "2026-08-14.1",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            Guid.NewGuid());

        approval.Veto(actorUserId, Guid.NewGuid());
        approval.Veto(actorUserId, Guid.NewGuid());

        Assert.True(approval.IsVetoed);
        Assert.Equal(FoundationApproval.VetoReason, approval.DeletedReason);
        Assert.Throws<InvalidOperationException>(() => approval.MarkConsumed(Guid.NewGuid(), "{}", actorUserId));
    }

    [Fact]
    public void An_approval_requires_an_expiration_after_its_creation()
    {
        Assert.Throws<ArgumentException>(() => new FoundationApproval(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ai.writes.record",
            new string('c', 64),
            "2026-08-14.1",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            Guid.NewGuid()));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
