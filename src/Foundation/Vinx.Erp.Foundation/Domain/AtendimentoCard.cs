namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Cartão do quadro de Atendimento. Concorrência otimista pelo <see cref="RowVersion"/>
/// e exclusão lógica por <see cref="DeletedAt"/>.
/// </summary>
public sealed class AtendimentoCard
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Column { get; set; } = "novo";

    public string Priority { get; set; } = "media";

    public string Channel { get; set; } = "manual";

    public DateTimeOffset? SlaDueAt { get; set; }

    public Guid? AssigneeMembershipId { get; set; }

    public Guid? AttachmentFileId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public string? CancellationReason { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid UpdatedByUserId { get; set; }

    public Guid RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedByUserId { get; set; }

    public string? DeletedReason { get; set; }

    public long RowVersion { get; set; } = 1;

    public ICollection<AtendimentoCardParticipant> Participants { get; } =
        new List<AtendimentoCardParticipant>();
}
