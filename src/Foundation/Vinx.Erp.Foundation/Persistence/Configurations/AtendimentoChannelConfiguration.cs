using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class AtendimentoChannelConfiguration : IEntityTypeConfiguration<AtendimentoChannel>
{
    public void Configure(EntityTypeBuilder<AtendimentoChannel> builder)
    {
        builder.ToTable("atendimento_channels", "service_desk");
        builder.HasKey(channel => channel.Id);
        builder.HasIndex(channel => new { channel.CompanyId, channel.Key }).IsUnique();
        builder.Property(channel => channel.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(channel => channel.CompanyId).HasColumnName("company_id");
        builder.Property(channel => channel.Key).HasColumnName("key").HasMaxLength(20);
        builder.Property(channel => channel.IsActive).HasColumnName("is_active");
    }
}
