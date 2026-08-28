using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class PaguaiRequestConfiguration : IEntityTypeConfiguration<PaguaiRequest>
{
    public void Configure(EntityTypeBuilder<PaguaiRequest> builder)
    {
        builder.ToTable("requests", "paguai");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
        builder
            .Property(request => request.CompanyId)
            .HasColumnName("company_id");
        builder
            .Property(request => request.RequesterUserId)
            .HasColumnName("requester_user_id");
        builder.Property(request => request.RequestId).HasColumnName("request_id");
        builder.Property(request => request.Title).HasColumnName("title").HasMaxLength(200);
        builder
            .Property(request => request.Description)
            .HasColumnName("description")
            .HasMaxLength(4000);
        builder.Property(request => request.State).HasColumnName("state");
        builder.Property(request => request.Version).HasColumnName("version");
        builder
            .Property(request => request.CreatedAt)
            .HasColumnName("created_at")
            .ValueGeneratedOnAdd();
    }
}
