namespace Vinx.Erp.Foundation.Domain;

/// <summary>Item de checklist de uma demanda.</summary>
public sealed class DemandChecklistItem
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid DemandId { get; set; }

    public string Text { get; set; } = string.Empty;

    public int Position { get; set; }

    public bool IsDone { get; set; }

    public DateTimeOffset? DoneAt { get; set; }

    public Guid? DoneByUserId { get; set; }
}

/// <summary>
/// Projeção append-only do histórico da demanda. Runtime recebe apenas
/// INSERT/SELECT; deduplicação operacional por <see cref="OccurrenceToken"/>.
/// </summary>
public sealed class DemandEvent
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid DemandId { get; set; }

    /// <summary>created | reinitiated | stage_advanced | responsible_changed |
    /// checklist_item_completed | paused | resumed | overdue | participant_added |
    /// participant_removed.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? FromStage { get; set; }

    public string? ToStage { get; set; }

    /// <summary>Ciclo de iniciação vigente no momento do evento.</summary>
    public int CycleCount { get; set; }

    /// <summary>Chave determinística do gatilho para idempotência do worker.</summary>
    public string OccurrenceToken { get; set; } = string.Empty;

    public Guid? ActorUserId { get; set; }

    public string MetadataJson { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; }
}
