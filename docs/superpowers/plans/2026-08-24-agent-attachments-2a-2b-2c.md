# Plano — Fatia 2: imagens/figuras no contexto do agente (2A/2B/2C)

Data: 2026-08-24. Issues: #298 (2A), #299 (2B), #300 (2C postponed).
Decomposição aprovada por Sol (plano rev.2 decisão (d), PR #296). Este plano
detalha a execução das três fatias; cada uma vira PR próprio com review Sol
individual. Revisão Sol rev.1 deste plano (2026-08-24,
`/tmp/vinx-f2-sol-plan-review.md`): CHANGES_REQUIRED com 5 bloqueadores e
rulings (a)–(i) — TODOS incorporados nesta rev. 2; decisões (a)(b)(d)(e)(f)
(h)(i) APROVADAS, (c)(g) incorporadas conforme ruling. Execução isolada:
worktrees `.worktrees/issue-134a-*`, `.worktrees/agent-attachments-*`,
`.worktrees/agent-chat-attachments-*`; prefixo `/tmp/vinx-f2-` para evidências.

## Fatos verificados (arquivo:linha)

- Pipeline #134 completo e sem consumidor real: `DocumentContextNormalizer`
  consome `IAuthorizedDocumentSource.OpenReadAsync(fileRecordId)`
  (`IAuthorizedDocumentSource.cs:21-24`) — implementação real não existe
  (só `FakeAuthorizedDocumentSource` em testes); OCR fail-closed em runtime.
- Módulo AI REFERENCIA Foundation (csproj) — `ActorContext`, CapabilityEvaluator,
  AuditAppender, FileStorage, FoundationDbContext visíveis ao módulo.
- FileRecord: estados quarantined→verified→confirmed/verification_failed;
  soft-delete + Restore; `RetentionUntil`, `Checksum`, `Classification`,
  `ProviderKey` (`Domain/FileRecord.cs`). Liberação = `confirmed` COM scan
  durável gravado (`DocumentUploadService.cs:409-412`) — hoje impossível em
  produção (BlockedMalwareScanner fail-closed), o que mantém 2A fail-closed
  até engine aprovada (aceito; teste usa engine fake como o resto da suíte).
- Storage devolve stream de rede NÃO-seekable (`SupabaseFileStorage.cs:153-170`)
  — PdfPig exige seek: 2A bufferiza com cap.
- Padrões de autorização: RLS via `FoundationRlsContext.ApplyCommandAsync`,
  membership lookup, `CapabilityEvaluator.HasGrantAsync` Company-scope,
  auditoria na mesma transação (`DocumentUploadService.cs:61-76,741-742`).
- Capabilities: id = UUIDv5(NAMESPACE_URL, `https://vinx.erp/capabilities/<nome>`)
  — esquema verificado contra 6 capabilities existentes; novo id
  `foundation.files.read-ocr` = `f9df3888-cadb-5344-bc68-3555b99dae86`.
  Seed por migration com Down fail-closed quando referenciada
  (`20260812032931_AddFoundationCapabilityCatalog.cs`) + teste de catálogo
  (`CapabilityCatalogMigrationTests.cs:16`).
- Runner single-turn (`AgentRunner.cs:65`): contexto de attachments entra
  como bloco do turno único, ANTES da única chamada ao modelo; hash de
  idempotência hoje cobre jornada/mensagem/empresa
  (`AgentExecutionApplicationService.cs:336-345`) — 2B estende.
- Composição de provider condicional já existe
  (`AgentRunServiceCollectionExtensions.cs:55-82`) — visão ganha seção
  própria nas `AgentRunOptions`.

## 2A — #298: fonte autorizada real (base `origin/main`)

**Branch:** `codex/issue-134a-authorized-document-source`.

1. **Contrato (AI module):** `OpenReadAsync(ActorContext actor, Guid
   companyId, Guid fileRecordId, CancellationToken ct)` — ator/empresa
   explícitos (finding Sol). `AuthorizedDocument` inalterado. Ajuste de
   assinatura no `DocumentContextNormalizer.BuildContextAsync` + fakes +
   testes #134 (mecânica, sem mudança de semântica).
2. **Implementação `FoundationAuthorizedDocumentSource`** (AI module,
   `DocumentContext/`, decisão Sol (a); comentário da interface e
   `docs/modules/ai.md` atualizados — deixam de dizer "owner Foundation"):
   preflight curto em `RepeatableRead` com RLS → membership ativa → capability
   `foundation.files.read-ocr` Company → FileRecord company-scoped
   (soft-delete excluído pelo query filter) → **liberação = `confirmed` E
   metadados de scan duráveis completos** (`ScanEngine`, `ScanEngineVersion`,
   `ScanSignatureVersion` não vazios E `ScannedAtUtc != null` — finding 2:
   `MarkConfirmed` sozinho não prova scan) → `RetentionUntil > now`.
   Negação do preflight é auditada e confirmada na mesma transação. Exceção
   tipada usa reason estável (`membership_required`,
   `capability_denied`, `resource_not_found` — documento de outra empresa é
   invisível —, `document_not_released`, `retention_expired`); o normalizer
   já converte qualquer exceção em fail-closed.
3. **Integridade pré-abertura (decisão (d)):** `GetObjectMetadataAsync` →
   checksum E size iguais ao registro (checksum `OrdinalIgnoreCase`);
   divergência = `checksum_mismatch` SEM abrir. Auditoria de decisão (allow
   E deny) na mesma transação: `AuditSource.Ai`, action
   `foundation.files.read-ocr`, resourceType `file`, resourceId, outcome +
   reason; sem conteúdo/PII. Depois de bufferizar, uma segunda transação curta
   `RepeatableRead` limpa tracking antigo, reautoriza e compara a tupla final
   `(ProviderKey, SizeBytes, Checksum, MimeType, Classification)` com a usada
   para ler. Mudança nega `document_changed`; falha de auditoria allow/deny
   nega `audit_unavailable` com um único log seguro, sem liberar conteúdo.
4. **Stream limitado/seekable (ruling (c)):** ANTES de qualquer alocação,
   exigir `1 <= SizeBytes <= DocumentUploadPolicy.MaxSizeBytes` (25 MiB) —
   registro fora da política = `document_size_out_of_policy` sem tocar o
   storage. Depois: `OpenReadAsync` do storage → cópia limitada a
   `SizeBytes + 1` bytes; byte excedente = `object_size_divergence`;
   buffer reposicionado em zero. Dispose pelo caller (normalizer).
5. **Capability:** `KnownCapabilities.FilesReadOcr` + id UUIDv5 + migration
   `AddFilesReadOcrCapability` (INSERT id/name; Down remove somente se não
   referenciada, senão EXCEPTION — padrão do catálogo) +
   `CapabilityCatalogMigrationTests` atualizado.
6. **Revogação:** leitura re-autorizada por chamada e novamente antes de
   liberar o buffer; nenhum cache/estado. O allow e qualquer negação final são
   auditados atomicamente com a decisão final.

**Testes (BDD):** sucesso (documento confirmed+scan durável+consentido abre
stream seekable com cap); acesso (empresa errada invisível =
resource_not_found; membership revogada nega; sem capability nega; documento
de outra empresa nunca vaza); estado (`quarantined`/`verification_failed`/
`deleted`/vencido negam; **linha `confirmed` SEM metadados de scan nega
`document_not_released` sem abrir storage** — finding 2); tamanho
(**registro `SizeBytes` 0 ou > 25 MiB nega `document_size_out_of_policy`
sem alocação** — finding 3); integridade (checksum/size divergentes negam
SEM chamar `OpenReadAsync` do storage; byte excedente na cópia =
`object_size_divergence`; stream curto também nega); corrida coordenada
(revogação ou mudança da tupla durante leitura nega antes de liberar);
auditoria indisponível e cancelamento explícito do caller. Integração
`Vinx.Erp.AI.IntegrationTests` com Postgres (Testcontainers) + storage fake
`Clean`; execuções sujeitas ao protocolo Docker do responsável.

**Inventário recalculado:** o handoff revisado sobre `a6e369e` tinha 16
arquivos (11 não-teste + 5 testes), 1.090 linhas manuais e 1.724 linhas
geradas de migration/designer/snapshot. Após a correção e o rebase na `main`
`0abd6e0`: 1.528 linhas manuais + 1.724 geradas. O total manual excede o alvo
de 800,
mas a exceção é coesa: contrato, fonte RLS/auditoria, migration, testes de
segurança e documentação formam uma única fatia #298; separar a prova da
correção enfraqueceria a revisão sem isolar um subsistema independente.

## 2B — #299: attachments no /agent/runs (stacked na 2A)

**Branch:** `codex/agent-attachments` (base = head 2A; re-target main após
merge serial).

**Estado em 2026-08-24:** implementado sobre `ca5910f` após os merges de 2A e
do chat FE textual. A evidência executável está no manifesto
`docs/verification/2026-08-24-issue-299-agent-attachments-test-cases.md`.

1. **Contrato HTTP:** `attachments` opcional no corpo: array de UUID
   strings, deduplicado, máx `ApiConstants.MaxAgentAttachments` (3), ordem
   canônica = ordem enviada pós-dedup. Parser cresce reasons
   (`attachments_invalid`, `attachments_too_many`); campos fechados
   continuam (novo campo no set).
2. **Idempotência (finding Sol):** `AgentRunIdempotencyPayload` ganha a
   lista canônica; mesmo `requestId` com attachments divergentes = 409
   auditado, nunca replay. Testes de divergência/igualdade.
3. **Runner:** perfil ganha `AllowDocumentAttachments` (default deny,
   decisão (f); journey único habilitado). Fluxo: resolução de contexto →
   se attachments e perfil nega → Invalid `attachments_not_allowed`; senão
   cada attachment via normalizer (2A) — QUALQUER NeedsClarification →
   desfecho needs_clarification com reason estável
   (`document_context_unavailable`) e **ZERO chamadas ao provider DO AGENTE
   (principal)** (decisão (e): o provider de VISÃO pode já ter sido usado
   por anexos anteriores do mesmo turno — não prometemos zero chamadas de
   visão sem preflight de todos os anexos); sucesso → turno único =
   mensagem + bloco `untrusted` com segmentos (`fileId:pN`), truncagem
   pelos limits.
4. **Host/composição (ruling (g) + finding 1):** `AgentRunOptions` ganha
   seção de visão opcional com provider/endpoint/api-key/modelo/timeout E a
   **allowlist server-side de classificações consentidas**
   (`AllowedClassifications`, obrigatória e não-vazia quando há visão —
   `DocumentContextLimits.cs:32,85` rejeita vazia). A allowlist é a ÚNICA
   autoridade de consentimento: a `Classification` informada pelo uploader é
   atestação NÃO confiável (validada apenas por presença/tamanho em
   `DocumentUploadPolicy.cs:79`), então o gate do normalizer permanece
   default-deny — **BDD: classificação forjada não habilita OCR de
   categoria fora da allowlist**. Visão ausente, configuração parcial ou
   allowlist vazia → normalizer NÃO registrado → attachments →
   needs_clarification `document_context_unavailable` (fail-closed).
5. **Seed canônica `ai.read` (finding 5):** `KnownCapabilities` ganha
   `AiRead` = `ai.read`, id `46db8e9e-5bd5-59ea-a371-0312ba8fafb8`
   (UUIDv5 NAMESPACE_URL, esquema verificado) + migration de seed no
   catálogo (mesmo padrão/Down fail-closed) + `CapabilityCatalogMigrationTests`
   atualizado + `AgentEndpointTests` passa a usar a id canônica (hoje cria
   aleatória, `AgentEndpointTests.cs:447`). Grant permanece default-deny: a
   seed registra a capability no catálogo; concessão a papéis é operação
   explícita do dono — sem grant, journey continua negado.
6. **OpenAPI:** request schema re-pinado; contract/boundary tests.

**Testes (BDD):** parser (matriz negativa completa); idempotência
(divergente 409 / igual replay); runner (anexo ok vira contexto untrusted
na única mensagem; anexo negado → needs_clarification sem chamada ao
provider do agente; perfil sem permissão → invalid; sem normalizer
registrado → needs_clarification); **allowlist de classificação (finding
1): classificação forjada/informada pelo uploader não habilita OCR de
categoria fora da allowlist server-side**; seed `ai.read` no catálogo
(integração) com grant default-deny; concorrência inalterada.

**Arquivos:** ~6 produção editados/novos + ~6 testes + docs. Estimativa
~700-900 linhas — teto justificado pela matriz negativa (mesmo regime das
fatias anteriores; registro no PR).

**Realizado:** 25 arquivos; 928 linhas manuais alteradas (889 adições e 39
remoções) e 1.718 linhas geradas da migration/designer. O total manual excede
800, mas permanece uma fatia vertical indivisível: aceitar referências sem
vinculá-las à idempotência, à autorização documental, à composição fail-closed
e ao catálogo `ai.read` deixaria uma fronteira parcialmente segura.

## 2C — #300: FE (postponed; base = 2B + #296)

**Branch:** `codex/agent-chat-attachments`.

1. **Upload client** (`agent-attachments-client.ts`): intention → PUT na
   URL assinada → confirm; antiforgery nas mutações BFF (intention/confirm);
   o PUT ao storage usa fetch PRÓPRIO com `credentials: "omit"`, redirects
   bloqueados, e a URL assinada NUNCA é logada nem persistida (decisão (i) —
   a assinatura bearer é a autorização). Intent completo (finding 1):
   `fileName`, `mimeType` (somente `application/pdf`/`image/jpeg`/
   `image/png` — allowlist real de `DocumentUploadPolicy.cs:13-20`),
   `sizeBytes` (1..25 MiB), `checksum` `sha256:<64 hex>` (WebCrypto SHA-256
   dos bytes), `classification` constante `agentic_attachment`, `origin`
   constante `web-agent-chat`, `retentionUntil` = now + 30 dias
   (`DocumentUploadPolicy.ValidateIntentMetadata` exige futuro). Falhas
   tipadas por etapa; sem retry.
2. **Dock:** picker (accept pdf/jpg/jpeg/png — **sem WebP**, finding 4),
   limite de cliente espelhado (tamanho/contagem), chip por anexo com
   remoção, upload concluído antes de habilitar envio; corpo ganha
   `attachments`; anexos pendentes NÃO sobrevivem a reset/troca de empresa
   (mesmo epoch/abort do #296).
3. **Mapeamento:** `needs_clarification/document_context_unavailable` →
   mensagem fixa + reason; erros de upload por etapa (intent/put/confirm)
   com mensagens fixas.
4. **Testes:** vitest client + provider (matriz por etapa, abort, troca de
   empresa) + e2e interceptando as três rotas.

**Arquivos:** ~4 novos + 4 editados + testes. ~600-800 linhas.

## Verificação e processo (comum)

- `dotnet test` suítes unit/arch não-Docker + vitest lint/format/build;
  integração Testcontainers sujeita ao protocolo Docker (solo, cooldown,
  evidência); `verify-public.sh` EXIT:0 no HEAD exato de cada PR quando a
  máquina permitir.
- Sol aprova ESTE plano antes da execução; Sol revisa cada PR; Terra
  opcional no diff final. PRs: 2A base main; 2B base 2A (re-target após
  merge); 2C base 2B+#296 (re-target). Sem escrever em main; sem tocar
  ledger/worktrees de outros agentes.
- Invariantes inegociáveis preservados: nunca arquivo bruto/metadata ao
  provider; conteúdo de documento é `untrusted`; default deny em
  classificação/consentimento/capability/jornada; 409 em idempotência
  divergente; sem PII/conteúdo em log/auditoria/resposta; credenciais só
  via env.

## Riscos e mitigações

- **Docker instável** (instrução do responsável): integração escrita e
  rodada solo quando possível; senão evidência documentada como nas fatias
  anteriores.
- **BlockedMalwareScanner**: sem engine real, nenhum documento chega a
  `confirmed` em produção → attachments fail-closed até o dono aprovar
  engine (decisão operacional registrada, não bloqueio de código).
- **Grant de capabilities**: as seeds de `foundation.files.read-ocr` (2A) e
  `ai.read` (2B) registram o catálogo; CONCESSÃO a papéis é operação
  explícita do dono (default-deny) — sem grant, tudo permanece negado
  (encontrado no finding 5 e mantido como invariantes).
- **Migrations**: design-time env obrigatório (FOUNDATION_MIGRATOR_*
  dummies) — histórico dos PRs #230/#231.
