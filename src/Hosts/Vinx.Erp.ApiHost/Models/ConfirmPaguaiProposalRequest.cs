using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Models;

public sealed record ConfirmPaguaiProposalRequest(Guid CompanyId, Guid RequestId)
{
    public ConfirmPaguaiProposalCommand ToCommand(Guid proposalId) =>
        new(CompanyId, proposalId, RequestId);
}
