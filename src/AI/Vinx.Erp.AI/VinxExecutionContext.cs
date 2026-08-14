namespace Vinx.Erp.AI;

/// <summary>
/// Contexto de execução confiável do harness. O ator e a empresa são fornecidos
/// exclusivamente por código do servidor (sessão autenticada); nunca são lidos
/// de argumentos, texto ou JSON produzidos pelo modelo.
/// </summary>
public sealed record VinxExecutionContext(string ActorId, string CompanyId)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ActorId) && !string.IsNullOrWhiteSpace(CompanyId);
}
