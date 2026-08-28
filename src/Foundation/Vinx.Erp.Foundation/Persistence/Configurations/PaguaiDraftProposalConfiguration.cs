using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class PaguaiDraftProposalConfiguration : IEntityTypeConfiguration<PaguaiDraftProposal>
{
    public const string LiveRequestIndexName = "uq_paguai_draft_proposals_live_request";

    public void Configure(EntityTypeBuilder<PaguaiDraftProposal> builder)
    {
        builder.ToTable(
            "draft_proposals",
            "paguai",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_paguai_draft_proposals_state",
                    "state IN ('pending', 'consumed', 'rejected', 'invalidated')");
                table.HasCheckConstraint("ck_paguai_draft_proposals_currency_brl", "currency = 'BRL'");
                table.HasCheckConstraint(
                    "ck_paguai_draft_proposals_confidence",
                    "confidence >= 0 AND confidence <= 1");
                table.HasCheckConstraint(
                    "ck_paguai_draft_proposals_lifetime",
                    "expires_at = created_at + interval '15 minutes'");
                table.HasCheckConstraint(
                    "ck_paguai_draft_proposals_resolution",
                    "(state = 'pending' AND resolved_at IS NULL AND resolution_request_id IS NULL AND result_draft_id IS NULL) OR "
                    + "(state = 'consumed' AND resolved_at IS NOT NULL AND resolution_request_id IS NOT NULL AND result_draft_id IS NOT NULL) OR "
                    + "(state IN ('rejected', 'invalidated') AND resolved_at IS NOT NULL AND resolution_request_id IS NOT NULL AND result_draft_id IS NULL)");
            });
        builder.HasKey(proposal => proposal.Id);
        builder.HasAlternateKey(proposal => new { proposal.CompanyId, proposal.Id });
        builder.Property(proposal => proposal.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(proposal => proposal.CompanyId).HasColumnName("company_id");
        builder.Property(proposal => proposal.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(proposal => proposal.RequestId).HasColumnName("request_id");
        builder.Property(proposal => proposal.FileRecordId).HasColumnName("file_record_id");
        builder.Property(proposal => proposal.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(proposal => proposal.Description).HasColumnName("description").HasMaxLength(4_000).IsRequired();
        builder.Property(proposal => proposal.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(proposal => proposal.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Property(proposal => proposal.DueDate).HasColumnName("due_date").HasColumnType("date").IsRequired();
        builder.Property(proposal => proposal.BeneficiaryDisplayName).HasColumnName("beneficiary_display_name").HasMaxLength(200).IsRequired();
        builder.Property(proposal => proposal.ProofRequirement).HasColumnName("proof_requirement").HasMaxLength(10).IsRequired();
        builder.Property(proposal => proposal.Confidence).HasColumnName("confidence").IsRequired();
        builder.Property(proposal => proposal.SourceReferences).HasColumnName("source_references").HasColumnType("jsonb").IsRequired();
        builder.Property(proposal => proposal.PolicyVersion).HasColumnName("policy_version").HasMaxLength(100).IsRequired();
        builder.Property(proposal => proposal.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64).IsRequired();
        builder.Property(proposal => proposal.State).HasColumnName("state").HasMaxLength(20).IsRequired();
        builder.Property(proposal => proposal.ResultDraftId).HasColumnName("result_draft_id");
        builder.Property(proposal => proposal.ResolutionRequestId).HasColumnName("resolution_request_id");
        builder.Property(proposal => proposal.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(proposal => proposal.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(proposal => proposal.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(proposal => proposal.RowVersion).HasColumnName("row_version").IsConcurrencyToken().HasDefaultValue(1L);
        builder.HasIndex(proposal => new { proposal.CompanyId, proposal.ActorUserId, proposal.RequestId })
            .IsUnique()
            .HasFilter("state = 'pending'")
            .HasDatabaseName(LiveRequestIndexName);
        builder.HasIndex(proposal => new { proposal.CompanyId, proposal.FileRecordId });
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(proposal => proposal.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(proposal => proposal.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.CompanyId, proposal.FileRecordId })
            .HasPrincipalKey(file => new { file.CompanyId, file.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
