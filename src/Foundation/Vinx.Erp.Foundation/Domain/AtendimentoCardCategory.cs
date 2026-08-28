namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Categoria de referência do quadro de Atendimento por empresa; ativação
/// substitui exclusão física e a edição fica sob capability própria.
/// </summary>
public sealed class AtendimentoCardCategory
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public Guid CreatedByUserId { get; set; }

    public Guid UpdatedByUserId { get; set; }

    public Guid RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
