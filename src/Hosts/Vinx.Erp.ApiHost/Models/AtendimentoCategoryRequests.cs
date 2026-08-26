using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

public sealed class CreateAtendimentoCategoryRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(80)]
    public string Name { get; init; } = string.Empty;

    public CreateAtendimentoCategoryCommand ToCommand() =>
        new(CompanyId, RequestId, Name);
}

public sealed class DeactivateAtendimentoCategoryRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    public DeactivateAtendimentoCategoryCommand ToCommand(Guid categoryId) =>
        new(CompanyId, RequestId, categoryId);
}
