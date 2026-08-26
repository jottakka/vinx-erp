using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class AgentExecutionLedgerConfiguration : IEntityTypeConfiguration<AgentExecutionLedger>
{
    public const string CommandKeyIndexDatabaseName =
        "ix_agent_execution_ledger_company_id_actor_user_id_channel_request_id";

    public void Configure(EntityTypeBuilder<AgentExecutionLedger> builder)
    {
        builder.ToTable("agent_execution_ledger", table =>
        {
            table.HasCheckConstraint(
                "ck_agent_execution_ledger_payload_hash_format",
                "payload_hash ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_agent_execution_ledger_state",
                "state IN ('claimed', 'vision_in_flight', 'model_in_flight', 'completed', 'failed_closed', 'ambiguous')");
            table.HasCheckConstraint(
                "ck_agent_execution_ledger_terminal_data",
                "(state = 'completed' AND safe_result_json IS NOT NULL AND reason_code IS NULL) OR " +
                "(state IN ('failed_closed', 'ambiguous') AND safe_result_json IS NULL AND reason_code IS NOT NULL) OR " +
                "(state IN ('claimed', 'vision_in_flight', 'model_in_flight') AND safe_result_json IS NULL AND reason_code IS NULL)");
        });
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.CompanyId, entity.Id });
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Channel).HasMaxLength(50).IsRequired();
        builder.Property(entity => entity.Journey).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.State)
            .HasConversion(
                state => ToDatabaseValue(state),
                value => FromDatabaseValue(value))
            .HasMaxLength(30)
            .IsRequired();
        builder.Property(entity => entity.SafeResultJson).HasColumnType("jsonb");
        builder.Property(entity => entity.ReasonCode).HasMaxLength(100);
        builder.HasIndex(entity => new { entity.CompanyId, entity.ActorUserId, entity.Channel, entity.RequestId })
            .IsUnique()
            .HasDatabaseName(CommandKeyIndexDatabaseName);
        builder.HasOne(entity => entity.Company)
            .WithMany()
            .HasForeignKey(entity => entity.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string ToDatabaseValue(AgentExecutionLedgerState state) => state switch
    {
        AgentExecutionLedgerState.Claimed => "claimed",
        AgentExecutionLedgerState.VisionInFlight => "vision_in_flight",
        AgentExecutionLedgerState.ModelInFlight => "model_in_flight",
        AgentExecutionLedgerState.Completed => "completed",
        AgentExecutionLedgerState.FailedClosed => "failed_closed",
        AgentExecutionLedgerState.Ambiguous => "ambiguous",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static AgentExecutionLedgerState FromDatabaseValue(string value) => value switch
    {
        "claimed" => AgentExecutionLedgerState.Claimed,
        "vision_in_flight" => AgentExecutionLedgerState.VisionInFlight,
        "model_in_flight" => AgentExecutionLedgerState.ModelInFlight,
        "completed" => AgentExecutionLedgerState.Completed,
        "failed_closed" => AgentExecutionLedgerState.FailedClosed,
        "ambiguous" => AgentExecutionLedgerState.Ambiguous,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
