using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

public sealed class CancelAtendimentoCardRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(500)]
    public string Reason { get; init; } = string.Empty;

    public CancelAtendimentoCardCommand ToCommand() =>
        new(CompanyId, RequestId, ExpectedVersion, Reason.Trim());
}
