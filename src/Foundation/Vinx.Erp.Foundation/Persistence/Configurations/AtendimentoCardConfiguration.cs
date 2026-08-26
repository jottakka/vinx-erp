using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class AtendimentoCardConfiguration : IEntityTypeConfiguration<AtendimentoCard>
{
    public void Configure(EntityTypeBuilder<AtendimentoCard> builder)
    {
        builder.ToTable("atendimento_cards", "service_desk");
        builder.HasKey(card => card.Id);
        builder.HasIndex(card => new { card.CompanyId, card.Id }).IsUnique();
        builder.HasQueryFilter(card => card.DeletedAt == null);
        builder.Property(card => card.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(card => card.CompanyId).HasColumnName("company_id");
        builder.Property(card => card.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(card => card.Description).HasColumnName("description").HasMaxLength(4000);
        builder.Property(card => card.Column).HasColumnName("column_name").HasMaxLength(30);
        builder
            .Property(card => card.AssigneeMembershipId)
            .HasColumnName("assignee_membership_id");
        builder
            .Property(card => card.RequesterPersonRef)
            .HasColumnName("requester_person_ref")
            .HasMaxLength(200);
        builder.Property(card => card.Priority).HasColumnName("priority").HasMaxLength(20);
        builder
            .Property(card => card.CategoryRefId)
            .HasColumnName("category_ref_id");
        builder.Property(card => card.ChannelRefId).HasColumnName("channel_ref_id");
        builder.Property(card => card.SlaDueAt).HasColumnName("sla_due_at");
        builder.Property(card => card.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(card => card.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(card => card.RequestId).HasColumnName("request_id");
        builder.Property(card => card.CreatedAt).HasColumnName("created_at");
        builder.Property(card => card.UpdatedAt).HasColumnName("updated_at");
        builder.Property(card => card.DeletedAt).HasColumnName("deleted_at");
        builder.Property(card => card.DeletedByUserId).HasColumnName("deleted_by_user_id");
        builder.Property(card => card.DeletedReason).HasColumnName("deleted_reason").HasMaxLength(500);
        builder
            .Property(card => card.RowVersion)
            .HasColumnName("row_version")
            .IsConcurrencyToken();
    }
}
