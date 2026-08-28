namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Observador de um cartão de Atendimento (membership da empresa); vínculo
/// reversível por remoção física da linha de junção.
/// </summary>
public sealed class AtendimentoCardWatcher
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid CardId { get; set; }

    public Guid MembershipId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
