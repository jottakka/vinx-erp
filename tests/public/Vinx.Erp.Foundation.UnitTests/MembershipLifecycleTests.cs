using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class MembershipLifecycleTests
{
    [Fact]
    public void Revoke_rejects_blank_reason_without_changing_lifecycle()
    {
        var membership = new Membership(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => membership.Revoke(" ", Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(membership.DeletedAt);
        Assert.Null(membership.DeletedByUserId);
        Assert.Null(membership.DeletedReason);
    }

    [Fact]
    public void Revoke_soft_deletes_once_and_preserves_membership_identity()
    {
        var membershipId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        var initialRequest = Guid.NewGuid();
        var membership = new Membership(membershipId, companyId, userId, createdBy, initialRequest);
        var revokedBy = Guid.NewGuid();
        var revokeRequest = Guid.NewGuid();

        membership.Revoke("membership_revoked", revokedBy, revokeRequest);

        Assert.Equal(membershipId, membership.Id);
        Assert.Equal(companyId, membership.CompanyId);
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(createdBy, membership.CreatedByUserId);
        Assert.Equal(revokedBy, membership.UpdatedByUserId);
        Assert.Equal(revokedBy, membership.DeletedByUserId);
        Assert.Equal("membership_revoked", membership.DeletedReason);
        Assert.NotNull(membership.DeletedAt);
        Assert.Equal(revokeRequest, membership.RequestId);
        Assert.Equal(1L, membership.RowVersion);
    }

    [Fact]
    public void Revoke_again_keeps_first_revocation_lifecycle()
    {
        var membership = new Membership(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        var firstActor = Guid.NewGuid();
        var firstRequest = Guid.NewGuid();
        membership.Revoke("membership_revoked", firstActor, firstRequest);
        var deletedAt = membership.DeletedAt;

        membership.Revoke("another_reason", Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(deletedAt, membership.DeletedAt);
        Assert.Equal(firstActor, membership.DeletedByUserId);
        Assert.Equal("membership_revoked", membership.DeletedReason);
        Assert.Equal(firstActor, membership.UpdatedByUserId);
        Assert.Equal(firstRequest, membership.RequestId);
        Assert.Equal(1L, membership.RowVersion);
    }
}
