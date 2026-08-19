namespace Vinx.Erp.AI;

/// <summary>
/// Provedor e modelo aprovados no servidor para um perfil de tarefa. Nula
/// enquanto o provedor não for atribuído (#135): o runner falha fechado com
/// provider_not_configured em vez de escolher modelo por conta própria.
/// </summary>
public sealed record AgentModelSelection(string ProviderId, string ModelId);
