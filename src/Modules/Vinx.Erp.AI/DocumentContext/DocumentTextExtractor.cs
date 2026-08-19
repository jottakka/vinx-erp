using UglyToad.PdfPig;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>Página com a camada de texto extraída localmente.</summary>
public sealed record ExtractedPdfPage(int Page, string Text, bool HasTextLayer);

/// <summary>
/// Extração local e determinística da camada de texto de PDFs via PdfPig.
/// O parse é síncrono: o timeout é cooperativo (checagem por página), sem
/// claim de cancelamento duro. PDF malformado vira falha segura do pipeline.
/// </summary>
public static class DocumentTextExtractor
{
    public static IReadOnlyList<ExtractedPdfPage> Extract(
        Stream pdf,
        DocumentContextLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(limits);

        try
        {
            using var document = PdfDocument.Open(pdf);
            if (document.NumberOfPages > limits.MaxPages)
            {
                throw new DocumentContextValidationException(
                    $"document exceeds the approved page limit of {limits.MaxPages}");
            }

            var pages = new List<ExtractedPdfPage>(document.NumberOfPages);
            foreach (var page in document.GetPages())
            {
                // Timeout cooperativo: checagem por página, não por byte.
                cancellationToken.ThrowIfCancellationRequested();

                var text = (page.Text ?? string.Empty).Trim();
                pages.Add(new ExtractedPdfPage(page.Number, text, text.Length > 0));
            }

            return pages;
        }
        catch (DocumentContextValidationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not DocumentContextValidationException)
        {
            // PDF malformado/ilegível: falha segura sem ecoar conteúdo.
            throw new DocumentContextValidationException("document could not be parsed locally");
        }
    }
}
