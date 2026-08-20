namespace Vinx.Erp.AI.DocumentContext;

/// <summary>Fonte do trecho: camada de texto local ou OCR por visão.</summary>
public enum DocumentSegmentSource
{
    TextLayer,
    VisionOcr,
}

/// <summary>
/// Trecho normalizado de documento. O texto é SEMPRE dado não confiável
/// (possível prompt injection): consumidores nunca o tratam como instrução
/// do sistema.
/// </summary>
public sealed record DocumentSegment(
    int Page,
    string SourceRef,
    string? BoundingBox,
    double Confidence,
    DocumentSegmentSource Source,
    string Text)
{
    /// <summary>Marca estrutural: conteúdo extraído não é instrução.</summary>
#pragma warning disable CA1822 // O marcador pertence ao contrato de segmento e deve permanecer acessível na instância.
    public bool Untrusted => true;
#pragma warning restore CA1822
}

/// <summary>Estado do contexto: pronto ou exige esclarecimento (falha segura).</summary>
public enum DocumentContextStatus
{
    Ready,
    NeedsClarification,
}

/// <summary>
/// Contexto normalizado do documento: trechos com fonte/página/confiança,
/// sem arquivo bruto. Nada é retido entre leituras (efêmero por contrato).
/// </summary>
public sealed record DocumentContextResult(
    Guid FileRecordId,
    DocumentContextStatus Status,
    string? ClarificationReason,
    IReadOnlyList<DocumentSegment> Segments,
    long TotalCharacters,
    bool Truncated);

/// <summary>Transcrição estrita de uma página pelo perfil de visão.</summary>
public sealed record VisionOcrPageResult(string Text, double Confidence, string? Language);

/// <summary>
/// Rejeição determinística do pipeline antes de qualquer chamada externa:
/// o documento viola os limites, modalidades ou consentimentos aprovados.
/// Mensagem fixa, sem ecoar conteúdo do documento.
/// </summary>
public sealed class DocumentContextValidationException(string message) : Exception(message);
