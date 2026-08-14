using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class FoundationApprovalConfiguration : IEntityTypeConfiguration<FoundationApproval>
{
    public void Configure(EntityTypeBuilder<FoundationApproval> builder)
    {
        builder.ToTable("approvals", table =>
        {
            table.HasCheckConstraint(
                "ck_approvals_expires_after_creation",
                "expires_at > created_at");
            table.HasCheckConstraint(
                "ck_approvals_payload_hash_format",
                "payload_hash ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_approvals_veto_complete",
                "(deleted_at IS NULL AND deleted_by_user_id IS NULL AND deleted_reason IS NULL) OR " +
                "(deleted_at IS NOT NULL AND deleted_by_user_id IS NOT NULL AND deleted_reason = 'approval_vetoed')");
        });
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Action).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.PolicyVersion).HasMaxLength(50).IsRequired();
        builder.Property(entity => entity.ResultJson).HasColumnType("jsonb");
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasIndex(entity => new { entity.CompanyId, entity.ActorUserId })
            .HasFilter("consumed_at IS NULL AND deleted_at IS NULL");
        builder.HasIndex(entity => entity.ProposalId)
            .IsUnique();
        builder.HasIndex(entity => entity.ConfirmationRequestId)
            .IsUnique()
            .HasFilter("confirmation_request_id IS NOT NULL");
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.ActorUser)
            .WithMany()
            .HasForeignKey(entity => entity.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
