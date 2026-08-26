namespace Vinx.Erp.LegacyImport;

/// <summary>Resultado por empresa de uma execução em modo escrita.</summary>
public enum CompanyImportOutcome
{
    /// <summary>Lote novo: receipt inserida e staging populado na mesma transação.</summary>
    ImportedNew,

    /// <summary>Replay idempotente: receipt idêntica já existia (mesmo hash e digest).</summary>
    ReplayedSkip,

    /// <summary>Lock consultivo ocupado por outra execução concorrente da mesma empresa.</summary>
    ConcurrentSkip,

    /// <summary>Lote mutado sob o mesmo batch: transação da empresa revertida inteira.</summary>
    ConflictedRollback,

    /// <summary>Dry-run: empresa validada sem qualquer escrita.</summary>
    Validated,
}

internal static class CompanyImportOutcomeLabels
{
    public static string ToLabel(this CompanyImportOutcome outcome) => outcome switch
    {
        CompanyImportOutcome.ImportedNew => "imported_new",
        CompanyImportOutcome.ReplayedSkip => "replayed_skip",
        CompanyImportOutcome.ConcurrentSkip => "concurrent_skip",
        CompanyImportOutcome.ConflictedRollback => "conflicted_rollback",
        CompanyImportOutcome.Validated => "validated",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>Vínculo determinístico entre chave de empresa legada e empresa alvo.</summary>
public sealed record CompanyMapping(
    string CompanyKey,
    Guid LegacyEmpresaId,
    Guid TargetCompanyId);

/// <summary>Resultado consolidado da empresa após uma execução com <c>--apply</c>.</summary>
public sealed record CompanyResult(
    string CompanyKey,
    CompanyImportOutcome Outcome,
    int Rows);

/// <summary>Saída de uma execução em modo escrita.</summary>
public sealed record ApplyResult(bool Succeeded, IReadOnlyList<CompanyResult> Companies);

/// <summary>Problema de validação do dry-run, sem dado de negócio embutido.</summary>
public sealed record ImportValidationProblem(string Code, string Detail);
