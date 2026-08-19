namespace Vinx.Erp.AI;

/// <summary>
/// Desfecho da resolução de contexto do agente.
/// </summary>
public enum AgentContextResolutionStatus
{
    Resolved,
    NeedsClarification,
    Denied,
    Unavailable,
}

/// <summary>
/// Resultado da resolução: <see cref="AgentContextResolutionStatus.Resolved"/>
/// carrega o contexto mínimo; os demais desfechos são fail-closed, com reason
/// code estável e <see cref="Context"/> nulo — nunca um contexto parcial.
/// </summary>
public sealed record AgentContextResolution(
    AgentContextResolutionStatus Status,
    string ReasonCode,
    AgentExecutionContext? Context = null);
