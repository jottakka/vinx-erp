using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreatePaguaiDraftRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(4000)]
    public string Description { get; init; } = string.Empty;

    [Required]
    [DecimalRange("0.01", "9999999999999999.99")]
    [JsonConverter(typeof(PaguaiAmountJsonConverter))]
    public decimal Amount { get; init; }

    [Required]
    [AllowedValues("BRL")]
    public string Currency { get; init; } = string.Empty;

    [Required]
    public DateOnly DueDate { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string BeneficiaryDisplayName { get; init; } = string.Empty;

    [Required]
    [AllowedValues("optional", "required")]
    public string ProofRequirement { get; init; } = string.Empty;

    [NonEmptyGuid]
    public Guid? InvoiceFileId { get; init; }

    /// <summary>Comprovante opcional classificado paguai_request_proof; documentação, não confirmação de pagamento.</summary>
    [NonEmptyGuid]
    public Guid? ProofFileId { get; init; }

    public CreateDraftCommand ToCommand() =>
        new(
            CompanyId: CompanyId,
            RequestId: RequestId,
            Title: Title,
            Description: Description,
            Amount: Amount,
            Currency: Currency,
            DueDate: DueDate,
            BeneficiaryDisplayName: BeneficiaryDisplayName,
            ProofRequirement: ProofRequirement,
            SourceInboundMessageId: null,
            InvoiceFileId: InvoiceFileId,
            ProofFileId: ProofFileId
        );
}
