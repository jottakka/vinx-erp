using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("groups");
        ConfigureMutable(builder);
        builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(entity => entity.Name).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
    }

    private static void ConfigureMutable(EntityTypeBuilder<Group> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
    }
}

public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("companies");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.GroupId, entity.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Group)
            .WithMany()
            .HasForeignKey(entity => entity.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable("units");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Unit)
            .WithMany()
            .HasForeignKey(entity => new { entity.CompanyId, entity.UnitId })
            .HasPrincipalKey(entity => new { entity.CompanyId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Email).HasMaxLength(320).IsRequired();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.HasIndex(entity => entity.Email).IsUnique();
    }
}

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions", table =>
        {
            table.HasCheckConstraint(
                "ck_user_sessions_authenticated_before_expiration",
                "provider_authenticated_at < token_expires_at");
            table.HasCheckConstraint(
                "ck_user_sessions_revocation_complete",
                "(revoked_at IS NULL AND revoked_by_user_id IS NULL AND revocation_reason IS NULL) OR " +
                "(revoked_at IS NOT NULL AND revoked_by_user_id IS NOT NULL AND revocation_reason IS NOT NULL AND btrim(revocation_reason) <> '')");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Issuer).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.FirstSeenAt)
            .HasDefaultValueSql("clock_timestamp()")
            .ValueGeneratedOnAdd();
        builder.Property(entity => entity.RevocationReason).HasMaxLength(500);
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.HasIndex(entity => new { entity.Issuer, entity.ProviderSessionId }).IsUnique();
        builder.HasIndex(entity => new { entity.UserId, entity.RevokedAt });
        builder.HasOne(entity => entity.User)
            .WithMany()
            .HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.RevokedByUser)
            .WithMany()
            .HasForeignKey(entity => entity.RevokedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.ToTable("external_identities");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Issuer).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Subject).HasMaxLength(400).IsRequired();
        builder.HasIndex(entity => new { entity.Issuer, entity.Subject }).IsUnique();
        builder.HasOne(entity => entity.User)
            .WithMany()
            .HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.UserId }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.User)
            .WithMany()
            .HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.MembershipId }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Membership)
            .WithMany()
            .HasForeignKey(entity => new { entity.CompanyId, entity.MembershipId })
            .HasPrincipalKey(entity => new { entity.CompanyId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CapabilityConfiguration : IEntityTypeConfiguration<Capability>
{
    public void Configure(EntityTypeBuilder<Capability> builder)
    {
        builder.ToTable("capabilities");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Description).HasMaxLength(1000);
        builder.HasIndex(entity => entity.Name).IsUnique();
    }
}

public sealed class RoleCapabilityConfiguration : IEntityTypeConfiguration<RoleCapability>
{
    public void Configure(EntityTypeBuilder<RoleCapability> builder)
    {
        builder.ToTable("role_capabilities", table =>
            table.HasCheckConstraint("ck_role_capabilities_scope", "scope IN ('self', 'team', 'unit', 'company', 'group')"));
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Scope)
            .HasConversion(
                scope => ToDatabaseScope(scope),
                value => FromDatabaseScope(value))
            .HasMaxLength(20);
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.RoleId, entity.CapabilityId, entity.Scope })
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Role)
            .WithMany()
            .HasForeignKey(entity => new { entity.CompanyId, entity.RoleId })
            .HasPrincipalKey(entity => new { entity.CompanyId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Capability)
            .WithMany()
            .HasForeignKey(entity => entity.CapabilityId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string ToDatabaseScope(CapabilityScope scope) => scope switch
    {
        CapabilityScope.Self => "self",
        CapabilityScope.Team => "team",
        CapabilityScope.Unit => "unit",
        CapabilityScope.Company => "company",
        CapabilityScope.Group => "group",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported capability scope.")
    };

    private static CapabilityScope FromDatabaseScope(string value) => value switch
    {
        "self" => CapabilityScope.Self,
        "team" => CapabilityScope.Team,
        "unit" => CapabilityScope.Unit,
        "company" => CapabilityScope.Company,
        "group" => CapabilityScope.Group,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported capability scope.")
    };
}

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.RoleId, entity.MembershipId })
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Role)
            .WithMany()
            .HasForeignKey(entity => new { entity.CompanyId, entity.RoleId })
            .HasPrincipalKey(entity => new { entity.CompanyId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Membership)
            .WithMany()
            .HasForeignKey(entity => new { entity.CompanyId, entity.MembershipId })
            .HasPrincipalKey(entity => new { entity.CompanyId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FileRecordConfiguration : IEntityTypeConfiguration<FileRecord>
{
    public void Configure(EntityTypeBuilder<FileRecord> builder)
    {
        builder.ToTable("files");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Ignore(entity => entity.IsQuarantined);
        builder.Property(entity => entity.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Extension).HasMaxLength(20).IsRequired();
        builder.Property(entity => entity.SizeBytes).IsRequired();
        builder.Property(entity => entity.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.Classification).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.Checksum).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Origin).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.RetentionUntil).IsRequired();
        builder.Property(entity => entity.ProviderKey).HasMaxLength(1000).IsRequired();
        builder.Property(entity => entity.ProcessingState).HasMaxLength(30).IsRequired();
        builder.Property(entity => entity.FailureReason).HasMaxLength(200);
        builder.Property(entity => entity.RowVersion).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Property(entity => entity.DeletedReason).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CompanyId, entity.ProviderKey }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasQueryFilter(entity => entity.DeletedAt == null);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.UploaderMembership)
            .WithMany()
            .HasForeignKey(entity => new { entity.CompanyId, entity.UploaderMembershipId })
            .HasPrincipalKey(entity => new { entity.CompanyId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public const string KeyIndexDatabaseName =
        "ix_idempotency_records_company_id_actor_user_id_command_name_r";

    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.CommandName).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.PayloadHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ResultJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(entity => new { entity.CompanyId, entity.ActorUserId, entity.CommandName, entity.RequestId })
            .IsUnique()
            .HasDatabaseName(KeyIndexDatabaseName);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events", "audit", table =>
            table.HasCheckConstraint(
                "ck_audit_events_requested_company",
                "(scope = 'global' AND requested_company_id IS NULL) OR (scope = 'company' AND requested_company_id IS NOT NULL)"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Scope)
            .HasConversion(
                scope => scope == AuditScope.Global ? "global" : "company",
                value => value == "global" ? AuditScope.Global : AuditScope.Company)
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(entity => entity.Source)
            .HasConversion(
                source => source == AuditSource.Web ? "web" :
                    source == AuditSource.Mcp ? "mcp" :
                    source == AuditSource.Ai ? "ai" : "worker",
                value => value == "web" ? AuditSource.Web :
                    value == "mcp" ? AuditSource.Mcp :
                    value == "ai" ? AuditSource.Ai : AuditSource.Worker)
            .HasMaxLength(20);
        builder.Property(entity => entity.Outcome)
            .HasConversion(
                outcome => outcome == AuditOutcome.Success ? "success" :
                    outcome == AuditOutcome.Denied ? "denied" : "failure",
                value => value == "success" ? AuditOutcome.Success :
                    value == "denied" ? AuditOutcome.Denied : AuditOutcome.Failure)
            .HasMaxLength(20);
        builder.Property(entity => entity.Action).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.ResourceType).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Reason).HasMaxLength(1000);
        builder.Property(entity => entity.MetadataJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(entity => new { entity.RequestedCompanyId, entity.OccurredAt });
        builder.HasIndex(entity => new { entity.ActorUserId, entity.OccurredAt });
    }
}
