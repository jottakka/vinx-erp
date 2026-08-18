using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

internal sealed record AtendimentoCardConflictResponse(
    string Code,
    AtendimentoCardResponse? Current
);
