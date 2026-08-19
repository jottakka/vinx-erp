using Vinx.Erp.Foundation.Files.ClamAv;

namespace Vinx.Erp.Foundation.Files;

/// <summary>
/// Composição fail-closed do <see cref="IMalwareScanner"/> de produção a partir de
/// <see cref="MalwareScannerOptions"/>: sem <see cref="MalwareScannerOptions.ClamAvEndpoint"/>
/// configurado devolve <see cref="BlockedMalwareScanner"/> (motivo seguro
/// <c>scanner_not_configured</c>); endpoint presente devolve o adapter real
/// <see cref="ClamAvMalwareScanner"/>. Endpoint em formato inválido explode como
/// <see cref="ArgumentException"/> na validação existente do adapter — o host valida o
/// formato on-start, nunca em runtime de scan.
/// </summary>
public static class MalwareScannerComposition
{
    public static IMalwareScanner SelectScanner(MalwareScannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.ClamAvEndpoint)
            ? new BlockedMalwareScanner()
            : new ClamAvMalwareScanner(options);
    }
}
