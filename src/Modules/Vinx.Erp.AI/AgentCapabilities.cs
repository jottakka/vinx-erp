namespace Vinx.Erp.AI;

/// <summary>
/// Capabilities aprovadas para journeys de agente. Identificadores futuros
/// derivam de UUIDv5 no namespace https://vinx.erp/capabilities/ ; a seed é
/// registrada na PR da API (#138). Sem seed registrada a produção falha
/// fechado (default deny).
/// </summary>
public static class AgentCapabilities
{
    public const string Read = "ai.read";
}
