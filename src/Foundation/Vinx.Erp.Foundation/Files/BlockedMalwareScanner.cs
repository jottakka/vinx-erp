namespace Vinx.Erp.Foundation.Files;

/// <summary>
/// Engine de produção até a aprovação da engine real pelo responsável: bloqueio explícito
/// e fail-closed — todo scan retorna <see cref="MalwareScanStatus.Unavailable"/> e nenhum
/// conteúdo é declarado limpo. Nunca abre o conteúdo da requisição.
/// </summary>
public sealed class BlockedMalwareScanner : IMalwareScanner
{
    public Task<MalwareScanVerdict> ScanAsync(MalwareScanRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new MalwareScanVerdict(
            MalwareScanStatus.Unavailable, null, null, null, DateTimeOffset.UtcNow, null, "scanner_not_configured"));
}
