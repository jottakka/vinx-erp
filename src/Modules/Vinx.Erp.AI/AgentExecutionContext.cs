namespace Vinx.Erp.AI;

/// <summary>
/// Contexto mínimo e confiável do agente, resolvido ANTES de qualquer chamada
/// de modelo: apenas identificadores e nomes normativos da única empresa
/// autorizada. Isolamento de dados (RLS) e auditoria pertencem à Foundation;
/// este snapshot nunca carrega prompt, JWT, e-mail ou qualquer segredo — o
/// conteúdo da mensagem do usuário não flui para o contexto do modelo.
/// </summary>
public sealed record AgentExecutionContext(
    Guid ActorId,
    Guid CorrelationId,
    Guid CompanyId,
    string CompanyName,
    Guid MembershipId,
    string ProfileId,
    IReadOnlySet<string> Capabilities);
