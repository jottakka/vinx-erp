using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Models;

public sealed class CreatePaguaiDraftRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid? CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid? RequestId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string? Title { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(4000)]
    public string? Description { get; init; }

    public CreateDraftCommand ToCommand() =>
        new(CompanyId!.Value, RequestId!.Value, Title!, Description!);
}
