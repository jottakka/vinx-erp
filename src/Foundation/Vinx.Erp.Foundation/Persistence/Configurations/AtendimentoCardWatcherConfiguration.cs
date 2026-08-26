using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class AtendimentoCardWatcherConfiguration : IEntityTypeConfiguration<AtendimentoCardWatcher>
{
    public void Configure(EntityTypeBuilder<AtendimentoCardWatcher> builder)
    {
        builder.ToTable("atendimento_card_watchers", "service_desk");
        builder.HasKey(watcher => watcher.Id);
        builder
            .HasIndex(
                watcher => new { watcher.CompanyId, watcher.CardId, watcher.MembershipId }
            )
            .IsUnique();
        builder.Property(watcher => watcher.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(watcher => watcher.CompanyId).HasColumnName("company_id");
        builder.Property(watcher => watcher.CardId).HasColumnName("card_id");
        builder.Property(watcher => watcher.MembershipId).HasColumnName("membership_id");
        builder
            .Property(watcher => watcher.CreatedByUserId)
            .HasColumnName("created_by_user_id");
        builder.Property(watcher => watcher.RequestId).HasColumnName("request_id");
        builder.Property(watcher => watcher.CreatedAt).HasColumnName("created_at");
    }
}
