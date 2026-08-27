using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

public sealed class AssignAtendimentoCardRequest
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
    [NonEmptyGuid]
    public Guid AssigneeMembershipId { get; init; }

    public AssignAtendimentoCardCommand ToCommand() =>
        new(CompanyId, RequestId, ExpectedVersion, AssigneeMembershipId);
}
