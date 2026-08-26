using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class PaguaiRequestConfiguration : IEntityTypeConfiguration<PaguaiRequest>
{
    public void Configure(EntityTypeBuilder<PaguaiRequest> builder)
    {
        builder.ToTable(
            "requests",
            "paguai",
            table => table.HasCheckConstraint("ck_paguai_requests_currency_brl", "currency = 'BRL'")
        );
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
        builder
            .Property(request => request.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(18,2)")
            .IsRequired();
        builder
            .Property(request => request.Currency)
            .HasColumnName("currency")
            .HasColumnType("text")
            .HasMaxLength(3)
            .IsRequired();
        builder
            .Property(request => request.DueDate)
            .HasColumnName("due_date")
            .HasColumnType("date")
            .IsRequired();
        builder
            .Property(request => request.BeneficiaryDisplayName)
            .HasColumnName("beneficiary_display_name")
            .HasColumnType("text")
            .HasMaxLength(200)
            .IsRequired();
        builder
            .Property(request => request.ProofRequirement)
            .HasColumnName("proof_requirement")
            .HasColumnType("text")
            .HasMaxLength(10)
            .IsRequired();
        builder.Property(request => request.ProofFileId).HasColumnName("proof_file_id");
        builder.Property(request => request.State).HasColumnName("state");
        builder.Property(request => request.Version).HasColumnName("version");
        builder
            .Property(request => request.CreatedAt)
            .HasColumnName("created_at")
            .ValueGeneratedOnAdd();
    }
}
