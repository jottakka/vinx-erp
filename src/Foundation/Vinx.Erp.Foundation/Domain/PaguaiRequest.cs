namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Rascunho Paguai por empresa: metadados e campos financeiros mínimos, sem fluxo financeiro.
/// </summary>
public sealed class PaguaiRequest
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RequesterUserId { get; set; }

    public Guid RequestId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "BRL";

    public DateOnly DueDate { get; set; }

    public string BeneficiaryDisplayName { get; set; } = string.Empty;

    public string ProofRequirement { get; set; } = string.Empty;

    public string State { get; set; } = "rascunho";

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }
}
