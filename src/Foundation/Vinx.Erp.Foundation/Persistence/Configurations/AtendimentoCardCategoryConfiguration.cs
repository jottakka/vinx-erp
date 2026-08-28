using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class AtendimentoCardCategoryConfiguration : IEntityTypeConfiguration<AtendimentoCardCategory>
{
    public void Configure(EntityTypeBuilder<AtendimentoCardCategory> builder)
    {
        builder.ToTable("atendimento_categories", "service_desk");
        builder.HasKey(category => category.Id);
        builder.HasIndex(category => new { category.CompanyId, category.Name }).IsUnique();
        builder.Property(category => category.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(category => category.CompanyId).HasColumnName("company_id");
        builder.Property(category => category.Name).HasColumnName("name").HasMaxLength(80);
        builder.Property(category => category.IsActive).HasColumnName("is_active");
        builder
            .Property(category => category.CreatedByUserId)
            .HasColumnName("created_by_user_id");
        builder
            .Property(category => category.UpdatedByUserId)
            .HasColumnName("updated_by_user_id");
        builder.Property(category => category.RequestId).HasColumnName("request_id");
        builder.Property(category => category.CreatedAt).HasColumnName("created_at");
        builder.Property(category => category.UpdatedAt).HasColumnName("updated_at");
    }
}
