namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Levantada quando um receipt existente entra em conflito com o lote atual
/// (mesmo batch/company/table com digest ou hash de mapeamento diferente).
/// A transação da empresa inteira deve ser revertida pelo chamador.
/// </summary>
public sealed class LegacyImportConflictException(string message) : Exception(message);

/// <summary>
/// Ponto de entrada do CLI. Modo padrão é dry-run (sem escrita alguma,
/// inclusive receipts); escritas exigem --apply.
/// Uso: [--apply] --mapping &lt;caminho&gt; [--batch &lt;id&gt;] | conexão via LEGACY_IMPORT_CONNECTION.
/// Saída: uma linha JSON de resumo em stdout; códigos 0 ok, 3 configuração
/// inválida, 1 falha/conflito fail-closed.
/// </summary>
public static class LegacyImportCommand
{
    // Esqueleto RED: toda execução falha até a implementação mínima do GREEN.
    public static Task<int> RunAsync(string[] arguments)
    {
        Console.Error.WriteLine("legacy-import: não implementado (fase RED)");
        return Task.FromResult(1);
    }
}
