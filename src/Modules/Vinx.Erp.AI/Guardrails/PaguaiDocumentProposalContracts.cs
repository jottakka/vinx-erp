namespace Vinx.Erp.AI.Guardrails;

/// <summary>Entrada efêmera do parser: um anexo autorizado e suas páginas liberadas.</summary>
public sealed record PaguaiDocumentProposalParseRequest(
    Guid FileRecordId,
    IReadOnlySet<string> AuthorizedSourceReferences,
    string StructuredJson);

/// <summary>Estados seguros do envelope Pagaí: proposta, esclarecimento ou schema inválido.</summary>
public enum PaguaiProposalParseStatus
{
    Proposal,
    NeedsClarification,
    Invalid,
}

/// <summary>Referência opaca de página autorizada usada para fundamentar um campo financeiro.</summary>
public sealed record PaguaiProposalSourceReference(string Field, string Source);

/// <summary>Proposta Pagaí tipada e segura; nunca carrega OCR, JSON bruto ou bytes do anexo.</summary>
public sealed record PaguaiDocumentProposal(
    string Title,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string BeneficiaryDisplayName,
    string ProofRequirement,
    Guid InvoiceFileId,
    double Confidence,
    IReadOnlyList<PaguaiProposalSourceReference> SourceReferences);

/// <summary>Resultado sem conteúdo bruto do parser de proposta Pagaí.</summary>
public sealed record PaguaiDocumentProposalParseResult(
    PaguaiProposalParseStatus Status,
    string? Reason,
    PaguaiDocumentProposal? Proposal);
