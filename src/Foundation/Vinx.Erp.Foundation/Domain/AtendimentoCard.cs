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

    public string Channel { get; set; } = "manual";

    public Guid? AssigneeMembershipId { get; set; }

    /// <summary>Nullável para compatibilidade: ausente significa sem prioridade definida.</summary>
    public string? Priority { get; set; }

    /// <summary>Solicitante como referência opaca; People Core pode resolver depois.</summary>
    public string? RequesterPersonRef { get; set; }

    /// <summary>Categoria por referência da tabela da empresa.</summary>
    public Guid? CategoryRefId { get; set; }

    /// <summary>Canal de origem por referência semeada (whatsapp/email/web/manual).</summary>
    public Guid? ChannelRefId { get; set; }

    /// <summary>Prazo de SLA; atraso é derivado em leitura (passado e não concluído).</summary>
    public DateTimeOffset? SlaDueAt { get; set; }

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
