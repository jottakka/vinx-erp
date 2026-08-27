namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Pessoa envolvida no cartão de Atendimento. A associação pertence à mesma empresa
/// do cartão e é imutável para o runtime após a criação.
/// </summary>
public sealed class AtendimentoCardParticipant
{
    public Guid CompanyId { get; set; }

    public Guid CardId { get; set; }

    public Guid MembershipId { get; set; }

    public AtendimentoCard Card { get; set; } = null!;
}
