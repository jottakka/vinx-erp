using System.Diagnostics.Metrics;

namespace Vinx.Erp.AI;

/// <summary>
/// Métricas do módulo de agente: counter único de execuções com tags de baixa
/// cardinalidade (status e reason code estáveis) — nunca prompt, resposta,
/// empresa ou PII em tag.
/// </summary>
public static class AgentTelemetry
{
    public const string MeterName = "Vinx.Erp.AI";
    public const string ExecutionsCounterName = "ai.agent.executions";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ExecutionsCounter = Meter.CreateCounter<long>(ExecutionsCounterName);

    internal static void RecordExecution(AgentOutcomeStatus status, string reasonCode)
    {
        ExecutionsCounter.Add(
            1,
            new KeyValuePair<string, object?>("result", ToResultTag(status)),
            new KeyValuePair<string, object?>("reason", reasonCode));
    }

    internal static string ToResultTag(AgentOutcomeStatus status) => status switch
    {
        AgentOutcomeStatus.Completed => "completed",
        AgentOutcomeStatus.NeedsClarification => "needs_clarification",
        AgentOutcomeStatus.RequiresConfirmation => "requires_confirmation",
        AgentOutcomeStatus.Denied => "denied",
        AgentOutcomeStatus.Invalid => "invalid",
        AgentOutcomeStatus.Unavailable => "unavailable",
        AgentOutcomeStatus.TimedOut => "timed_out",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported agent outcome status."),
    };
}
