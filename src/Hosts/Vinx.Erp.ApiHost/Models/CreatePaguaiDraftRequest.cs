using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vinx.Erp.ApiHost.Constants;
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
    [Range(1, ApiConstants.MaximumAmountMinor)]
    public long AmountMinor { get; init; }

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

    public CreateDraftCommand ToCommand() =>
        new(
            CompanyId,
            RequestId,
            Title,
            Description,
            AmountMinor,
            Currency,
            DueDate,
            BeneficiaryDisplayName,
            ProofRequirement
        );
}
