using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Orquestrador do contexto de documento: fonte autorizada → allowlist de
/// classificação → extração local (camada de texto) → raster+OCR somente
/// para páginas sem texto → normalização com fonte/página/confiança e
/// truncagem determinística. Leituras são efêmeras: nenhum estado, cache ou
/// resposta é retido entre execuções. Falhas viram `NeedsClarification` com
/// razão fixa — nunca sucesso inventado.
/// </summary>
public sealed class DocumentContextNormalizer
{
    private readonly IAuthorizedDocumentSource _source;
    private readonly DocumentVisionOcr _visionOcr;
    private readonly DocumentContextLimits _limits;

    public DocumentContextNormalizer(
        IAuthorizedDocumentSource source,
        DocumentVisionOcr visionOcr,
        DocumentContextLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(visionOcr);
        ArgumentNullException.ThrowIfNull(limits);

        var failures = limits.Validate();
        if (failures.Count > 0)
        {
            throw new DocumentContextValidationException("document context limits are not approved");
        }

        _source = source;
        _visionOcr = visionOcr;
        _limits = limits;
    }

    public async Task<DocumentContextResult> BuildContextAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        CancellationToken cancellationToken)
        => await BuildContextAsync(actor, companyId, fileRecordId, lifecycle: null, cancellationToken).ConfigureAwait(false);

    internal async Task<DocumentContextResult> BuildContextAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        IAgentExecutionLifecycle? lifecycle,
        CancellationToken cancellationToken)
    {
        AuthorizedDocument document;
        try
        {
            document = await _source
                .OpenReadAsync(actor, companyId, fileRecordId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fonte negou (RLS/membership/capability/estado/retenção): fail-closed.
            return NeedsClarification(fileRecordId, "document is not available for reading");
        }

        using (document.Content)
        {
            // Allowlist de classificação/consentimento ANTES de qualquer
            // raster/OCR; default deny.
            if (!_limits.AllowedClassifications.Contains(document.Classification))
            {
                return NeedsClarification(fileRecordId, "document classification is not approved for OCR");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_limits.ExtractionTimeout);

            try
            {
                var segments = document.MediaType switch
                {
                    "application/pdf" => await FromPdfAsync(document, lifecycle, timeout.Token).ConfigureAwait(false),
                    "image/png" or "image/jpeg" or "image/webp" =>
                        await FromImageAsync(document, lifecycle, timeout.Token).ConfigureAwait(false),
                    _ => throw new DocumentContextValidationException("document media type is not supported"),
                };

                return Finalize(fileRecordId, segments);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return NeedsClarification(fileRecordId, "document context extraction timed out");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Qualquer falha de preparação (I/O, stream descartado, decode,
                // provider, schema) vira resultado seguro/redigido; somente o
                // cancelamento explícito do chamador propaga.
                return NeedsClarification(fileRecordId, "document context could not be produced safely");
            }
        }
    }

    private async Task<List<DocumentSegment>> FromPdfAsync(
        AuthorizedDocument document,
        IAgentExecutionLifecycle? lifecycle,
        CancellationToken cancellationToken)
    {
        var pages = DocumentTextExtractor.Extract(document.Content, _limits, cancellationToken);
        var segments = new List<DocumentSegment>(pages.Count);
        foreach (var page in pages)
        {
            if (page.HasTextLayer)
            {
                segments.Add(Segment(document.FileRecordId, page.Page, null, 1.0, DocumentSegmentSource.TextLayer, page.Text));
                continue;
            }

            // Página escaneada: uma rasterização e uma chamada de visão por página.
            var raster = await PageRasterizer.RasterizePageAsync(
                document.Content, page.Page, page.WidthPoints, page.HeightPoints, _limits, cancellationToken).ConfigureAwait(false);
            var ocr = await _visionOcr.TranscribePageAsync(raster, "image/jpeg", lifecycle, cancellationToken).ConfigureAwait(false);
            segments.Add(Segment(document.FileRecordId, page.Page, null, ocr.Confidence, DocumentSegmentSource.VisionOcr, ocr.Text));
        }

        return segments;
    }

    private async Task<List<DocumentSegment>> FromImageAsync(
        AuthorizedDocument document,
        IAgentExecutionLifecycle? lifecycle,
        CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream();
        await document.Content.CopyToAsync(content, cancellationToken).ConfigureAwait(false);

        // Canonicalizar: o provider nunca recebe os bytes originais.
        var canonical = ImageCanonicalizer.Canonicalize(content.ToArray(), _limits);
        var ocr = await _visionOcr.TranscribePageAsync(canonical.Bytes, canonical.MediaType, lifecycle, cancellationToken).ConfigureAwait(false);
        return [Segment(document.FileRecordId, 1, null, ocr.Confidence, DocumentSegmentSource.VisionOcr, ocr.Text)];
    }

    private DocumentContextResult Finalize(Guid fileRecordId, List<DocumentSegment> segments)
    {
        var truncated = false;
        long total = 0;
        var final = new List<DocumentSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var text = segment.Text;
            if (text.Length > _limits.MaxCharactersPerSegment)
            {
                text = text[..SafePrefixLength(text, _limits.MaxCharactersPerSegment)];
                truncated = true;
            }

            if (total + text.Length > _limits.MaxCharactersPerContext)
            {
                var remaining = (int)(_limits.MaxCharactersPerContext - total);
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                text = text[..SafePrefixLength(text, remaining)];
                truncated = true;
            }

            total += text.Length;
            final.Add(segment with { Text = text });
        }

        return new DocumentContextResult(
            fileRecordId,
            DocumentContextStatus.Ready,
            null,
            final,
            total,
            truncated);
    }

    private static int SafePrefixLength(string text, int requestedLength)
    {
        if (requestedLength <= 0)
        {
            return 0;
        }

        if (requestedLength >= text.Length)
        {
            return text.Length;
        }

        return char.IsHighSurrogate(text[requestedLength - 1])
            && char.IsLowSurrogate(text[requestedLength])
                ? requestedLength - 1
                : requestedLength;
    }

    private static DocumentSegment Segment(
        Guid fileRecordId,
        int page,
        string? boundingBox,
        double confidence,
        DocumentSegmentSource source,
        string text) =>
        new(page, $"{fileRecordId:N}:p{page}", boundingBox, confidence, source, text);

    private static DocumentContextResult NeedsClarification(Guid fileRecordId, string reason) =>
        new(fileRecordId, DocumentContextStatus.NeedsClarification, reason, [], 0, false);
}
