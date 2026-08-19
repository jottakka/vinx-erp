# Plano — #134 OCR e contexto normalizado (implementação, rev. 2 pós-Terra)

> **Para agentes:** plano da fatia #134 a partir do handoff
> `priority-agent-ocr-context.md`. Revisão Terra rev.1 (2026-08-19,
> `/tmp/vinx-134-terra-plan-review.md`): CHANGES_REQUIRED com 3 bloqueadores
> e 2 erros — incorporados abaixo. Luna executa fixtures/testes; Sol revisa
> exposição de documento, tenant, consentimento e prompt injection; Terra
> revisa documentação, valida findings e revisa o diff final. Base: fatia
> #135-B commitada no branch `codex/issue-135b-openai-compatible-provider`
> (SHA registrado no PR; re-alvejar ao main após merge serial).

**Base de stack:** commit `291c4e5` (branch
`codex/issue-135b-openai-compatible-provider`, pushada; re-alvejar ao main
após merge serial).

**Goal:** PDF/imagem autorizado vira contexto normalizado e verificável para
o agente: extração local de texto quando possível, raster + OCR por visão
somente quando necessário, saída JSON estrita com fonte/página/confiança —
sem arquivo bruto, sem metadado original, sem prompt injetado e sem dado
não consentido ao provider. Leituras são **efêmeras**: nenhum contexto é
persistido ou cacheado nesta fatia.

**Architecture:** pipeline puro no módulo AI (`DocumentContext/`) atrás de
duas abstrações: (a) `IAuthorizedDocumentSource` — fronteira Foundation que
devolve stream limitado somente após RLS, membership ativa, capability
dedicada de leitura/OCR, estado liberado, retenção e checksum, sem URL
assinada; (b) `IChatClient` de perfil de visão do #135-B. O pipeline não
escolhe provider, não autoriza, não persiste e trata texto de documento
como dado não confiável.

## Pré-requisito Foundation (fora desta fatia — propor fatia #134-A)

Terra confirmou que a produção ainda usa `BlockedMalwareScanner` (nenhum
engine aprovado) e `FileRecord` libera por `confirmed`, não `clean`; não
existe leitura de stream autorizada nem capability de leitura/OCR. Portanto:

1. **#134-A (Foundation, fatia separada a propor):** implementação real de
   `IAuthorizedDocumentSource` (stream limitado/seekable, checks acima,
   capability de menor privilégio `files.read-ocr` — nunca reusar upload/
   confirm) + auditoria de decisão. Sem isso, OCR em runtime fica
   **fail-closed** (indisponível) no host.
2. **#134 (esta fatia):** entrega o pipeline completo contra a abstração,
   com fontes sintéticas `Clean` controladas nos testes — nenhum teste
   depende de engine real.

## Decisões de implementação (rev. 2)

1. **Camada de texto PDF:** PdfPig 0.1.15 (Apache-2.0, gerenciado). `Open`
   é síncrono: timeout é **cooperativo** (checagem por página), sem claim
   de cancelamento duro; exceção de PDF malformado vira falha segura
   classificada.
2. **Raster de página:** PDFtoImage 5.4.0 (MIT; PDFium+SkiaSharp nativos).
   PDFium não é thread-safe (chamadas globalmente serializadas upstream):
   rasterização atrás de `SemaphoreSlim(1,1)` com política de admissão
   documentada. Versões diretas e transitivas pinadas no lockfile; RID de
   produção (linux-x64) registrado e evidência de teste no PR/CI.
3. **Limites compostos com o perfil de visão (#135-B):** uma página por
   chamada (sem batch); bytes codificados `≤ min(teto OCR, perfil
   .MaxImageBytesPerImage)`; DPI/qualidade escolhidos para caber sob o
   teto com fallback determinístico (re-encode JPEG qualidade fixa) ou
   rejeição segura; testes de fronteira exata (dimensão decodada, bytes
   codificados, contagem de páginas, uma chamada por página, timeout após
   trabalho parcial, zero chamadas ao provider quando preparação falha).
4. **Canonicalização de imagem direta (não enviar bytes originais):**
   decode → limite de pixels → strip de metadados → re-encode determinístico
   (SkiaSharp, já transitivo). O provider nunca recebe o arquivo original
   nem metadados EXIF.
5. **Allowlist de classificação/consentimento ANTES de raster/OCR:** perfil
   declara classificações permitidas (default deny); classe que exigir
   redação é rejeitada até #136 fornecer redator testado (link #136).
   Decisões allow/deny são auditáveis no ponto de fronteira (#134-A).
6. **OCR por visão:** `DocumentVisionOcr` envia a página canônica ao
   `IChatClient` do perfil de visão; resposta em JSON estrito
   (texto/confiança/idioma); inválido/timeout → `needs_clarification`,
   nunca sucesso inventado; sem retry; dado só viaja para perfil
   aprovado/consentido (revisão Sol).
7. **Sem cache/estado:** cada leitura reprocessa sob autorização corrente;
   BDD de estado prova que NADA é retido entre leituras. Cache persistente
   (entidade, migração, RLS, retenção, invalidação) é fatia própria futura
   se prova de necessidade aparecer.
8. **Prompt injection:** texto extraído é dado não confiável no contrato
   (`untrusted: true`); consumidores (#132/#136) nunca o tratam como
   instrução — documentado no contrato.

## Arquivos exclusivos

- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/IAuthorizedDocumentSource.cs` (contrato; implementação real é #134-A)
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentTextExtractor.cs`
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/PageRasterizer.cs` (serializado, tetos compostos)
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/ImageCanonicalizer.cs` (decode/pixel-limit/strip/re-encode)
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentVisionOcr.cs`
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/OcrContextContracts.cs`
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentContextNormalizer.cs`
- Criar `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentContextLimits.cs`
- Modificar `Directory.Packages.props` + lockfiles (PdfPig 0.1.15, PDFtoImage 5.4.0 e transitivos pinados).
- Criar testes em `tests/public/Vinx.Erp.AI.UnitTests/DocumentContext/`
  (PDFs/PNGs sintéticos gerados em código; vision OCR com fake
  HttpMessageHandler do perfil; fontes `Clean` fake).
- Criar `tests/public/Vinx.Erp.AI.IntegrationTests/` (novo projeto com
  referência a Foundation e AI) com `DocumentContextAccessTests.cs`: provas
  de CONTRATO DE FRONTEIRA (empresa errada, membership revogada, motivo
  interno não vazado — via fake), sem Postgres. As provas RLS-backed com
  Testcontainers pertencem à #134-A junto com a fonte real; o PR declara
  explicitamente esse escopo.
- Criar `docs/verification/issue-134-ocr-context-test-cases.md`.
- Atualizar `docs/modules/ai.md` com o contrato de contexto.

Não alterar providers (#135-B), WhatsApp, API HTTP, UI, `Program.cs` nem
domínio Paguai. Não criar scanner próprio (#104 é dono), não criar
capability na Foundation (é #134-A), não criar cache.

## Tasks

1. RED: PDF textual sintético, PDF escaneado sintético, imagem direta
   canônica, página sem texto, idioma/encoding, limites compostos
   (fronteira exata de pixels/bytes/páginas), timeout cooperativo,
   documento não liberado/não consentido, JSON de visão inválido,
   tenant negativo, membership revogada, fail-closed sem engine,
   zero estado retido, concorrência de leituras.
2. `DocumentContextLimits` vinculado ao perfil de visão; imposição ANTES
   de raster/OCR; `IAuthorizedDocumentSource` com fakes `Clean`.
3. `DocumentTextExtractor` (PdfPig) limitado e cooperativo.
4. `PageRasterizer` serializado (SemaphoreSlim) + `ImageCanonicalizer`
   (strip de metadados, re-encode determinístico).
5. `DocumentVisionOcr` sobre `IChatClient` de visão; JSON estrito;
   `needs_clarification` em schema/timeout; sem retry.
6. `DocumentContextNormalizer` com sourceRef opaco, página, bbox,
   confidence, classification, `untrusted`; truncagem determinística.
7. Projeto de integração AI + testes de acesso (fail-closed, tenant,
   revogação) — sem engine real.

## Estimativa

- **Arquivos fora do cap de teste (13):** 8 de produção em
  `DocumentContext/`, 1 projeto de teste de integração (csproj + fixture
  base), 2 docs, `Directory.Packages.props` (+2 lockfiles gerados
  reportados à parte).
- **Testes (fora do cap de arquivos):** ~7–8 arquivos (unit + integração).
- **Linhas manuais:** ~1.300 incluindo testes — **acima do teto de 800**,
  mesmo regime do #135-B: matrizes negativas obrigatórias (limites
  compostos, consentimento, fail-closed, tenant) multiplicam testes;
  justificativa registrada no PR com aprovação explícita do responsável.

## BDD mínimo

- **Sucesso:** PDF textual autorizado+consentido gera JSON normalizado com
  fonte e página, sem arquivo bruto; PDF escaneado rasteriza uma página por
  chamada dentro dos tetos compostos e transcreve via perfil de visão
  (fake); imagem direta chega canônica (sem metadados originais).
- **Validação/erro:** limite composto (pixels/bytes/páginas), documento não
  liberado, classificação fora da allowlist, JSON inválido, timeout
  cooperativo → `needs_clarification`/falha segura, sem sucesso inventado,
  sem retry, zero chamadas ao provider quando a preparação falha.
- **Acesso:** documento Alfa nunca aparece no contexto Beta (fonte fake
  sobre RLS real); membership revogada impede leitura nova; sem engine
  aprovado o OCR é fail-closed (integração Postgres).
- **Estado:** nada é retido entre leituras — reprocesso sob autorização
  corrente; não existe cache para vazar.
- **Concorrência:** rasterização serializada; duas leituras não misturam
  fontes/páginas entre documentos.

## Verificação

`dotnet test tests/public/Vinx.Erp.AI.UnitTests` + ArchitectureTests +
novo `Vinx.Erp.AI.IntegrationTests` (quando a máquina estiver livre —
protocolo de carga) + `./scripts/verify-public.sh` EXIT:0 no HEAD exato
SERIALIZADO no freeze (inclui RID linux-x64 no CI); Terra revisa o diff
final; Sol revisa exposição de documento, tenant, consentimento e prompt
injection antes do freeze; comentário de evidência no PR com SHA `291c4e5` do
#135-B, transitivos nativos pinados e evidência da revisão de licença dos
nativos (PDFium/BSD-3, SkiaSharp/MIT) com links e versões exatas.

## Execução isolada

Worktree descartável próprio (ex.: `.worktrees/issue-134-ocr-context`),
branch `codex/issue-134-ocr-context` **a partir do commit `291c4e5` do #135-B**
(re-alvejar ao main após merge serial), prefixo `/tmp/vinx-134-` para
evidências. Não tocar worktrees/branches/PRs de outros agentes nem o ledger
`HANDOFF-mvp-builder.md` (conflitos esperados: `docs/modules/ai.md`,
`Directory.Packages.props`, lockfiles, sln se aplicável). Antes de teste
pesado/gate: `pgrep -fl dotnet; docker stats --no-stream` — se houver run
ativo de outro agente, aguardar e serializar (Docker ~3,8 GiB compartilhado).
