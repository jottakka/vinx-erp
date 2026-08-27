using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class DemandConfiguration : IEntityTypeConfiguration<Demand>
{
    public void Configure(EntityTypeBuilder<Demand> builder)
    {
        builder.ToTable("demands", "demandas");
        builder.HasKey(demand => demand.Id);
        builder.HasIndex(demand => new { demand.CompanyId, demand.Id }).IsUnique();
        builder.HasQueryFilter(demand => demand.DeletedAt == null);
        builder.Property(demand => demand.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(demand => demand.CompanyId).HasColumnName("company_id");
        builder
            .Property(demand => demand.DemandNumber)
            .HasColumnName("demand_number")
            .UseIdentityAlwaysColumn();
        builder.Property(demand => demand.Title).HasColumnName("title").HasMaxLength(200);
        builder
            .Property(demand => demand.Description)
            .HasColumnName("description")
            .HasMaxLength(4000);
        builder.Property(demand => demand.Priority).HasColumnName("priority").HasMaxLength(12);
        builder.Property(demand => demand.Stage).HasColumnName("stage").HasMaxLength(30);
        builder.Property(demand => demand.RoutineId).HasColumnName("routine_id");
        builder
            .Property(demand => demand.ResponsibleMembershipId)
            .HasColumnName("responsible_membership_id");
        builder.Property(demand => demand.RequesterUserId).HasColumnName("requester_user_id");
        builder.Property(demand => demand.Tipo).HasColumnName("tipo").HasMaxLength(100);
        builder
            .Property(demand => demand.Departamento)
            .HasColumnName("departamento")
            .HasMaxLength(100);
        builder.Property(demand => demand.ManualDueAt).HasColumnName("manual_due_at");
        builder.Property(demand => demand.SlaDueAt).HasColumnName("sla_due_at");
        builder.Property(demand => demand.StageDueAt).HasColumnName("stage_due_at");
        builder.Property(demand => demand.StageStartedAt).HasColumnName("stage_started_at");
        builder
            .Property(demand => demand.AutomationPaused)
            .HasColumnName("automation_paused");
        builder.Property(demand => demand.OverdueSince).HasColumnName("overdue_since");
        builder
            .Property(demand => demand.StageCycleCount)
            .HasColumnName("stage_cycle_count")
            .HasDefaultValue(1);
        builder.Property(demand => demand.ClosedAt).HasColumnName("closed_at");
        builder
            .Property(demand => demand.PendingApprovalId)
            .HasColumnName("pending_approval_id");
        builder
            .Property(demand => demand.PendingProposalId)
            .HasColumnName("pending_proposal_id");
        builder.Property(demand => demand.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(demand => demand.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(demand => demand.RequestId).HasColumnName("request_id");
        builder.Property(demand => demand.CreatedAt).HasColumnName("created_at");
        builder.Property(demand => demand.UpdatedAt).HasColumnName("updated_at");
        builder.Property(demand => demand.DeletedAt).HasColumnName("deleted_at");
        builder
            .Property(demand => demand.DeletedByUserId)
            .HasColumnName("deleted_by_user_id");
        builder
            .Property(demand => demand.DeletedReason)
            .HasColumnName("deleted_reason")
            .HasMaxLength(500);
        builder
            .Property(demand => demand.RowVersion)
            .HasColumnName("row_version")
            .IsConcurrencyToken();
    }
}

public sealed class DemandRoutineConfiguration : IEntityTypeConfiguration<DemandRoutine>
{
    public void Configure(EntityTypeBuilder<DemandRoutine> builder)
    {
        builder.ToTable("demand_routines", "demandas");
        builder.HasKey(routine => routine.Id);
        builder.HasIndex(routine => new { routine.CompanyId, routine.Id }).IsUnique();
        builder.HasQueryFilter(routine => routine.DeletedAt == null);
        builder.Property(routine => routine.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(routine => routine.CompanyId).HasColumnName("company_id");
        builder.Property(routine => routine.Name).HasColumnName("name").HasMaxLength(120);
        builder
            .Property(routine => routine.Description)
            .HasColumnName("description")
            .HasMaxLength(500);
        builder.Property(routine => routine.Tipo).HasColumnName("tipo").HasMaxLength(100);
        builder
            .Property(routine => routine.Departamento)
            .HasColumnName("departamento")
            .HasMaxLength(100);
        builder
            .Property(routine => routine.AutoAssigneeMembershipId)
            .HasColumnName("auto_assignee_membership_id");
        builder.Property(routine => routine.RoutingOrder).HasColumnName("routing_order");
        builder
            .Property(routine => routine.DefaultChecklistItems)
            .HasColumnName("default_checklist_items")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(routine => routine.IsActive).HasColumnName("is_active");
        builder
            .Property(routine => routine.CreatedByUserId)
            .HasColumnName("created_by_user_id");
        builder
            .Property(routine => routine.UpdatedByUserId)
            .HasColumnName("updated_by_user_id");
        builder.Property(routine => routine.RequestId).HasColumnName("request_id");
        builder.Property(routine => routine.CreatedAt).HasColumnName("created_at");
        builder.Property(routine => routine.UpdatedAt).HasColumnName("updated_at");
        builder.Property(routine => routine.DeletedAt).HasColumnName("deleted_at");
        builder
            .Property(routine => routine.DeletedByUserId)
            .HasColumnName("deleted_by_user_id");
        builder
            .Property(routine => routine.DeletedReason)
            .HasColumnName("deleted_reason")
            .HasMaxLength(500);
        builder
            .Property(routine => routine.RowVersion)
            .HasColumnName("row_version")
            .IsConcurrencyToken();
    }
}

public sealed class DemandRoutineStageConfiguration : IEntityTypeConfiguration<DemandRoutineStage>
{
    public void Configure(EntityTypeBuilder<DemandRoutineStage> builder)
    {
        builder.ToTable("demand_routine_stages", "demandas");
        builder.HasKey(stage => stage.Id);
        builder
            .HasIndex(stage => new { stage.CompanyId, stage.RoutineId, stage.OrderIndex })
            .IsUnique();
        builder
            .HasIndex(stage => new { stage.CompanyId, stage.RoutineId, stage.Stage })
            .IsUnique();
        builder.Property(stage => stage.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(stage => stage.CompanyId).HasColumnName("company_id");
        builder.Property(stage => stage.RoutineId).HasColumnName("routine_id");
        builder.Property(stage => stage.OrderIndex).HasColumnName("order_index");
        builder.Property(stage => stage.Stage).HasColumnName("stage").HasMaxLength(30);
        builder.Property(stage => stage.TriggerKind).HasColumnName("trigger_kind");
        builder.Property(stage => stage.TriggerHours).HasColumnName("trigger_hours");
        builder.Property(stage => stage.DeadlineHours).HasColumnName("deadline_hours");
    }
}

public sealed class DemandParticipantConfiguration : IEntityTypeConfiguration<DemandParticipant>
{
    public void Configure(EntityTypeBuilder<DemandParticipant> builder)
    {
        builder.ToTable("demand_participants", "demandas");
        builder.HasKey(participant => participant.Id);
        builder
            .HasIndex(participant => new { participant.CompanyId, participant.DemandId, participant.UserId })
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
        builder.Property(participant => participant.Id).HasColumnName("id").ValueGeneratedNever();
        builder
            .Property(participant => participant.CompanyId)
            .HasColumnName("company_id");
        builder.Property(participant => participant.DemandId).HasColumnName("demand_id");
        builder.Property(participant => participant.UserId).HasColumnName("user_id");
        builder.Property(participant => participant.Role).HasColumnName("role").HasMaxLength(20);
        builder
            .Property(participant => participant.CreatedByUserId)
            .HasColumnName("created_by_user_id");
        builder.Property(participant => participant.CreatedAt).HasColumnName("created_at");
        builder.Property(participant => participant.DeletedAt).HasColumnName("deleted_at");
        builder
            .Property(participant => participant.DeletedByUserId)
            .HasColumnName("deleted_by_user_id");
        builder
            .Property(participant => participant.DeletedReason)
            .HasColumnName("deleted_reason")
            .HasMaxLength(500);
    }
}

public sealed class DemandChecklistItemConfiguration : IEntityTypeConfiguration<DemandChecklistItem>
{
    public void Configure(EntityTypeBuilder<DemandChecklistItem> builder)
    {
        builder.ToTable("demand_checklist_items", "demandas");
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.CompanyId, item.DemandId, item.Position });
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.CompanyId).HasColumnName("company_id");
        builder.Property(item => item.DemandId).HasColumnName("demand_id");
        builder.Property(item => item.Text).HasColumnName("text").HasMaxLength(200);
        builder.Property(item => item.Position).HasColumnName("position");
        builder.Property(item => item.IsDone).HasColumnName("is_done");
        builder.Property(item => item.DoneAt).HasColumnName("done_at");
        builder.Property(item => item.DoneByUserId).HasColumnName("done_by_user_id");
    }
}

public sealed class DemandEventConfiguration : IEntityTypeConfiguration<DemandEvent>
{
    public void Configure(EntityTypeBuilder<DemandEvent> builder)
    {
        builder.ToTable("demand_events", "demandas");
        builder.HasKey(@event => @event.Id);
        builder
            .HasIndex(@event => new { @event.DemandId, @event.Kind, @event.OccurrenceToken })
            .IsUnique();
        builder
            .HasIndex(@event => new { @event.CompanyId, @event.DemandId, @event.OccurredAt });
        builder.Property(@event => @event.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(@event => @event.CompanyId).HasColumnName("company_id");
        builder.Property(@event => @event.DemandId).HasColumnName("demand_id");
        builder.Property(@event => @event.Kind).HasColumnName("kind").HasMaxLength(40);
        builder.Property(@event => @event.FromStage).HasColumnName("from_stage").HasMaxLength(30);
        builder.Property(@event => @event.ToStage).HasColumnName("to_stage").HasMaxLength(30);
        builder.Property(@event => @event.CycleCount).HasColumnName("cycle_count");
        builder
            .Property(@event => @event.OccurrenceToken)
            .HasColumnName("occurrence_token")
            .HasMaxLength(64);
        builder.Property(@event => @event.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(@event => @event.MetadataJson).HasColumnName("metadata_json");
        builder.Property(@event => @event.OccurredAt).HasColumnName("occurred_at");
    }
}
