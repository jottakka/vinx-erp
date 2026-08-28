namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Canal de origem semeado por migração (whatsapp/email/web/manual);
/// somente leitura para o runtime.
/// </summary>
public sealed class AtendimentoChannel
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Key { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
