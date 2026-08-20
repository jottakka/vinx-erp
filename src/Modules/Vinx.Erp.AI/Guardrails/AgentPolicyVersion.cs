namespace Vinx.Erp.AI.Guardrails;

/// <summary>Versão datada da política de saída do agente.</summary>
public static class AgentPolicyVersion
{
    /// <summary>
    /// Versão vigente; registrada em todo resultado do gate. A revisão
    /// 2026-08-19.2 remove a redação determinística e a lista fechada de
    /// injeção PT/EN: restam somente as verificações 100% determinísticas
    /// (schema, fontes, grounding/confiança).
    /// </summary>
    public const string Current = "2026-08-19.2";
}
