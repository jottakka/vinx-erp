using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Models;

public sealed record RejectPaguaiProposalRequest(Guid CompanyId, Guid RequestId)
{
    public RejectPaguaiProposalCommand ToCommand(Guid proposalId) =>
        new(CompanyId, proposalId, RequestId);
}
