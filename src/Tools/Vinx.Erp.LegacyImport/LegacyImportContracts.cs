namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Contratos do importador legado. Modo padrão é <see cref="DryRun"/>:
/// valida mapeamentos sem escrever nada, inclusive receipts.
/// </summary>
public enum LegacyImportMode
{
    DryRun,
    Apply,
}

public enum CompanyImportOutcome
{
    ImportedNew,
    ReplayedSkip,
    ConcurrentSkip,
}

public sealed record CompanyMapping(
    string CompanyKey,
    Guid LegacyEmpresaId,
    Guid TargetCompanyId);

public sealed record CompanyResult(
    string CompanyKey,
    CompanyImportOutcome Outcome,
    int StagedRows,
    long ClientRowsInLegacy);

public sealed record ImportValidationProblem(string Code, string Detail);

public sealed record ImportSummary(
    LegacyImportMode Mode,
    IReadOnlyList<CompanyResult> Companies,
    IReadOnlyList<ImportValidationProblem> Problems);

/// <summary>Resultado de uma execução em modo Apply.</summary>
public sealed record ApplyResult(bool Succeeded, IReadOnlyList<CompanyResult> Companies)
{
    /// <summary>Código de processo correspondente: 0 sucesso, 1 conflito/falha fechada.</summary>
    public int ExitCode => Succeeded ? 0 : 1;
}
