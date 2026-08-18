using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

public sealed class MoveAtendimentoCardRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid? CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid? RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long? ExpectedVersion { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(30)]
    public string? Column { get; init; }

    public MoveAtendimentoCardCommand ToCommand() =>
        new(CompanyId!.Value, RequestId!.Value, ExpectedVersion!.Value, Column!);
}
