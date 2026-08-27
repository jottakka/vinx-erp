namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Participante de demanda. Um único supervisor vigente por demanda (índice
/// único parcial) e observadores livres. Remoção é soft delete auditável.
/// </summary>
public sealed class DemandParticipant
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid DemandId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>supervisor | watcher.</summary>
    public string Role { get; set; } = DemandParticipantRoles.Watcher;

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedByUserId { get; set; }

    public string? DeletedReason { get; set; }
}

public static class DemandParticipantRoles
{
    public const string Supervisor = "supervisor";
    public const string Watcher = "watcher";
}
