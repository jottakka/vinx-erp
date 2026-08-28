namespace Vinx.Erp.AI;

/// <summary>Estado seguro devolvido pela execução durável do agente.</summary>
public enum AgentExecutionDisposition
{
    Completed,
    InProgress,
    Ambiguous,
    Conflict,
    FailedClosed,
}

/// <summary>Resultado sem conteúdo bruto; pode carregar somente o ID da proposta Pagaí materializada.</summary>
public sealed record AgentExecutionResult(
    AgentExecutionDisposition Disposition,
    AgentOutcome? Outcome = null,
    string? ReasonCode = null,
    Guid? PaguaiProposalId = null)
{
    public static AgentExecutionResult Completed(AgentOutcome outcome, Guid? paguaiProposalId = null) =>
        new(AgentExecutionDisposition.Completed, outcome, PaguaiProposalId: paguaiProposalId);

    public static AgentExecutionResult InProgress() =>
        new(AgentExecutionDisposition.InProgress, null, "execution_in_progress");

    public static AgentExecutionResult Ambiguous(string reasonCode, AgentOutcome? observedOutcome = null) =>
        new(AgentExecutionDisposition.Ambiguous, observedOutcome, reasonCode);

    public static AgentExecutionResult Conflict() =>
        new(AgentExecutionDisposition.Conflict, null, "idempotency_conflict");

    public static AgentExecutionResult FailedClosed(string reasonCode) =>
        new(AgentExecutionDisposition.FailedClosed, null, reasonCode);
}
