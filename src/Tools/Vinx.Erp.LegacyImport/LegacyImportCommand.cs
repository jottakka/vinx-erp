using System.Data.Common;
using System.Text.Json;
using Npgsql;

namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Levantada quando um receipt existente entra em conflito com o lote atual
/// (mesmo batch/company/table com digest ou hash de mapeamento diferente).
/// A transação da empresa inteira deve ser revertida pelo chamador.
/// </summary>
public sealed class LegacyImportConflictException(string message) : Exception(message);

/// <summary>
/// Ponto de entrada do CLI. Modo padrão é dry-run (sem escrita alguma,
/// inclusive receipts); escritas exigem <c>--apply</c>.
///
/// Uso: [--apply] --mapping &lt;caminho&gt;
/// Conexão via variável de ambiente LEGACY_IMPORT_CONNECTION.
/// Saída: uma linha JSON de resumo em stdout.
/// Códigos de saída: 0 sucesso, 1 falha/conflito fail-closed, 3 configuração inválida.
/// </summary>
public static class LegacyImportCommand
{
    private static readonly string DryRunMode = "dry_run";
    private static readonly string ApplyMode = "apply";

    public static async Task<int> RunAsync(string[] arguments)
    {
        var apply = false;
        string? mappingPath = null;
        var index = 0;
        while (index < arguments.Length)
        {
            switch (arguments[index])
            {
                case "--apply":
                    apply = true;
                    index++;
                    break;
                case "--mapping":
                    if (index + 1 >= arguments.Length || mappingPath is not null)
                    {
                        return InvalidUsage();
                    }

                    mappingPath = arguments[index + 1];
                    index += 2;
                    break;
                default:
                    return InvalidUsage();
            }
        }

        var connectionString = Environment.GetEnvironmentVariable("LEGACY_IMPORT_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString) || mappingPath is null || !File.Exists(mappingPath))
        {
            return InvalidUsage();
        }

        LegacyImportMappingFile.Parsed mapping;
        try
        {
            mapping = LegacyImportMappingFile.Load(mappingPath);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or IOException)
        {
            await Console.Error.WriteLineAsync($"legacy-import: mapeamento inválido: {exception.Message}");
            return 3;
        }

        try
        {
            var importer = new LegacyImporter();
            if (!apply)
            {
                var validation = await importer.DryRunAsync(
                    new LegacyImporter.Options(
                        connectionString!, mapping.SourceBatchId, mapping.Companies),
                    CancellationToken.None);
                WriteSummary(DryRunMode, validation.Problems.Count == 0, mapping.SourceBatchId, validation.Companies, validation.Problems);
                return validation.Valid ? 0 : 1;
            }

            // Escritas exigem staging existente conforme convenção DDL do tooling.
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var ddl = new NpgsqlCommand(LegacyImportDdl.EnsureStaging, connection);
                await ddl.ExecuteNonQueryAsync();
            }

            var result = await importer.ApplyAsync(
                new LegacyImporter.Options(connectionString, mapping.SourceBatchId, mapping.Companies),
                CancellationToken.None);
            WriteSummary(ApplyMode, result.Succeeded, mapping.SourceBatchId, result.Companies, []);
            return result.Succeeded ? 0 : 1;
        }
        catch (LegacyImportConflictException)
        {
            await Console.Error.WriteLineAsync(
                "legacy-import: conflito fail-closed (lote mutado ou hash divergente sob o mesmo batch); nenhuma escrita parcial foi mantida");
            return 1;
        }
        catch (DbException exception) when (exception is PostgresException postgres)
        {
            // Apenas código estável + SqlState: mensagens do servidor podem carregar dados.
            await Console.Error.WriteLineAsync(
                $"legacy-import: falha de banco (sqlstate={postgres.SqlState}); valide conexão e papel e reexecute em modo dry-run");
            return 1;
        }
        catch (DbException)
        {
            await Console.Error.WriteLineAsync(
                "legacy-import: falha de banco (sqlstate=desconhecido); valide conexão e reexecute em modo dry-run");
            return 1;
        }
    }

    private static int InvalidUsage()
    {
        Console.Error.WriteLine(
            "uso: LEGACY_IMPORT_CONNECTION=... legacy-import [--apply] --mapping <arquivo.json>");
        return 3;
    }

    /// <summary>Resumo processável, uma linha; contém só chaves e contagens.</summary>
    private static void WriteSummary(
        string mode,
        bool succeeded,
        string sourceBatchId,
        IReadOnlyList<CompanyResult> companies,
        IReadOnlyList<ImportValidationProblem> problems)
    {
        var payload = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["succeeded"] = succeeded,
            ["source_batch_id"] = sourceBatchId,
            ["companies"] = companies
                .Select(c => new Dictionary<string, object?>
                {
                    ["company_key"] = c.CompanyKey,
                    ["outcome"] = c.Outcome.ToLabel(),
                    ["rows"] = c.Rows,
                })
                .ToList(),
        };
        if (problems.Count > 0)
        {
            payload["problems"] = problems
                .Select(p => new Dictionary<string, string?> { ["code"] = p.Code, ["detail"] = p.Detail })
                .ToList();
        }

        Console.WriteLine(JsonSerializer.Serialize(payload));
    }
}
