# Manifesto público — issue 134, OCR e contexto normalizado

## Escopo e comandos

O módulo AI entrega o pipeline `DocumentContext` sobre a abstração
`IAuthorizedDocumentSource` (implementação real é a fatia Foundation #134-A)
consumindo o perfil de visão do #135-B. Todos os testes usam fixtures
sintéticos gerados em código (PDFs mínimos canônicos + PNGs SkiaSharp) e fake
HttpMessageHandler: nenhum endpoint real, nenhum segredo, nenhum dado médico.

| Código | Comando exato |
|---|---|
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q` |
| AI-INTEG | `dotnet test tests/public/Vinx.Erp.AI.IntegrationTests --no-restore --nologo -v q` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q` |

**Limite de ambiente conhecido:** o nativo do PDFium não carrega no sandbox
local (dylib presente, load bloqueado — verificado com PDF real do sistema,
`/tmp/vinx-134-pdfprobe`). Os testes que exercem rasterização têm guarda de
canário (`PdfiumEnvironment`) e executam integralmente no CI linux-x64 do
gate; localmente são neutros por guarda documentada, não por omissão. Os
testes RLS-backed contra Postgres real pertencem à #134-A.

## Cinco classes BDD

| Classe | Garantia | Evidência pública |
|---|---|---|
| sucesso | PDF textual autorizado+consentido gera contexto Ready com fonte/página/confiança, sem arquivo bruto e sem chamada ao provider | `DocumentContextNormalizerTests.Text_pdf_produces_ready_context_without_any_provider_call` |
| sucesso | PDF escaneado rasteriza e transcreve cada página sem texto, uma chamada por página | `DocumentContextNormalizerTests.Scanned_pdf_rasterizes_and_ocrs_each_page_without_text` (guarda PDFium) |
| sucesso | PDF misto usa camada de texto e visão por página | `DocumentContextNormalizerTests.Mixed_pdf_uses_text_layer_and_vision_per_page` (guarda PDFium) |
| sucesso | Imagem direta chega canônica (re-encode, sem bytes originais/metadata) | `DocumentContextNormalizerTests.Direct_image_is_canonicalized_and_ocrd_as_a_single_page`; `ImageCanonicalizerTests.Direct_image_is_re_encoded_without_original_bytes_or_metadata` |
| sucesso | Teto composto com o perfil de visão (min(teto OCR, MaxImageBytesPerImage)) | `DocumentContextLimitsTests.Encoded_byte_limit_is_composed_with_the_vision_profile`; `ImageCanonicalizerTests.Noisy_image_falls_back_to_lossy_encoding_within_the_byte_cap` |
| validação/erros | Página/limite de caracteres/pixels/bytes excedidos falham antes ou dentro da preparação, sem sucesso inventado | `DocumentTextExtractorTests.Page_count_above_the_limit_is_rejected_before_any_raster`; `ImageCanonicalizerTests.Image_above_the_pixel_limit_is_rejected`; `PageRasterizerTests.Huge_page_is_rejected_by_the_pixel_preflight_before_any_render` (preflight de pixels ANTES do nativo, sem guarda); `PageRasterizerTests.Raster_above_the_byte_cap_after_the_dpi_ladder_fails_safely` (guarda PDFium) |
| validação/erros | Stream de conteúdo que falha (I/O) vira `NeedsClarification` com zero chamadas ao provider | `DocumentContextNormalizerTests.Failing_content_stream_is_needs_clarification_with_zero_provider_calls` |
| validação/erros | Classificação fora da allowlist (default deny) nega antes de qualquer trabalho | `DocumentContextNormalizerTests.Classification_outside_the_allowlist_is_denied_before_any_work` |
| validação/erros | JSON de visão inválido, provider 500 e timeout viram `NeedsClarification`, sem retry; parse estrito exige exatamente text/confidence/language (sem ausentes, extras ou duplicatas) | `DocumentContextNormalizerTests.Invalid_vision_json_is_needs_clarification` (guarda PDFium); `DocumentVisionOcrTests.Non_json_provider_response_fails_safely`; `DocumentVisionOcrTests.Provider_failure_surfaces_as_provider_exception_without_retry`; `DocumentVisionOcrTests.Parse_strict_rejects_invalid_schemas` |
| validação/erros | PDF/imagem ilegíveis falham seguro sem ecoar conteúdo | `DocumentTextExtractorTests.Malformed_pdf_fails_safely_without_echoing_content`; `ImageCanonicalizerTests.Undecodable_image_fails_safely` |
| acesso | Negação da fronteira (empresa errada/membership revogada/estado) é fail-closed sem vazar motivo interno e sem chamar provider | `DocumentContextAccessTests.Company_mismatch_at_the_boundary_is_fail_closed`; `DocumentContextAccessTests.Revoked_membership_is_fail_closed_on_every_new_read`; `DocumentContextAccessTests.Denied_read_never_leaks_the_internal_reason` |
| estado | Nada é retido entre leituras (efêmero por contrato); cancelamento do chamador propaga | `DocumentContextNormalizerTests.Nothing_is_retained_between_reads`; `DocumentContextNormalizerTests.Caller_cancellation_propagates_instead_of_needs_clarification` |
| estado | Truncagem determinística no limite do contexto | `DocumentContextNormalizerTests.Context_is_truncated_deterministically_at_the_character_limit` |
| concorrência | Rasterização serializada por gate global (PDFium não thread-safe) | `PageRasterizer` (SemaphoreSlim) + `PageRasterizerTests.Scanned_page_rasterizes_within_the_composed_byte_cap` (guarda PDFium) |
| concorrência | Timeout cooperativo checado por página | `DocumentTextExtractorTests.Cancellation_is_cooperative_and_surfaces_as_operation_canceled`; `PageRasterizerTests.Caller_cancellation_surfaces_as_operation_canceled` |

## Limite explícito

Esses testes provem o pipeline com fronteira fake e PDFium onde disponível.
Não provem a fonte real autorizada (#134-A), os testes RLS-backed contra
Postgres, exposure HTTP (#138), guardrails de saída (#136) nem o host
registro DI. A evidência de revisão de licença dos nativos
(PDFium/BSD-3-Clause, SkiaSharp/MIT) com versões exatas é exigida no PR.
