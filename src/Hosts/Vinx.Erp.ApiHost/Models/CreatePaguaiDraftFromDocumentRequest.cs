using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Models;

/// <summary>
/// Rascunho a partir de documento inbound (#272 PR4): os campos que a fatura
/// não garante (title/description/proofRequirement) são sempre explícitos;
/// valor/beneficiário/vencimento vêm do candidato tipado admitido.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreatePaguaiDraftFromDocumentRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid InboundMessageId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(4000)]
    public string Description { get; init; } = string.Empty;

    [Required]
    [AllowedValues("optional", "required")]
    public string ProofRequirement { get; init; } = string.Empty;

    public CreateDraftFromDocumentCommand ToCommand() =>
        new(CompanyId, InboundMessageId, Title, Description, ProofRequirement);
}
