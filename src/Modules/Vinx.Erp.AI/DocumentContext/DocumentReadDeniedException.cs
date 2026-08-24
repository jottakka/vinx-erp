namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Negação determinística da fronteira de leitura autorizada. O motivo é
/// estável (nunca ecoa conteúdo, chave de storage ou metadado do documento);
/// o consumidor converte em falha segura (needs_clarification).
/// </summary>
public sealed class DocumentReadDeniedException(string reasonCode)
    : Exception($"Authorized document read was denied with reason {reasonCode}.")
{
    public string ReasonCode { get; } = reasonCode;
}
