namespace Vinx.Erp.AI;

/// <summary>
/// Contexto de execução confiável do harness. O ator e a empresa são fornecidos
/// exclusivamente por código do servidor (sessão autenticada); nunca são lidos
/// de argumentos, texto ou JSON produzidos pelo modelo. <see cref="Guid.Empty"/>
/// é sempre inválido (fail-closed).
/// </summary>
public sealed record VinxExecutionContext(Guid ActorId, Guid CompanyId)
{
    public bool IsValid => ActorId != Guid.Empty && CompanyId != Guid.Empty;
}
