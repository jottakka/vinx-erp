# Engine antimalware ClamAV/clamd (#104b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** implementar a engine antimalware real aprovada (ClamAV via daemon clamd) para o #104: adapter `IMalwareScanner` server-side com versões de engine/assinatura, veredito vinculado ao conteúdo, persistência durável dos metadados de scan e gate por configuração (sem endpoint → continua `BlockedMalwareScanner` fail-closed).

**Architecture:** empilhado sobre `codex/issue-104-antimalware` (PR #246, HEAD `63cda46`). O `ClamAvMalwareScanner` fala o protocolo INSTREAM do clamd por TCP com um cliente mínimo interno (avaliar nClam antes — ver Task 2), computa SHA-256 enquanto faz streaming (vincula o veredito ao conteúdo), consulta `VERSION` para engine/assinatura e respeita limite de bytes e timeout. A confirmação passa a persistir `scan_engine/engine_version/signature_version/scanned_at` no `FileRecord` nas saídas duráveis (clean confirmado e `malware_detected` terminal); transitório continua sem transação de estado. Testes contra um servidor clamd FAKO in-process (determinístico, sem download de assinaturas); E2E com clamd real fica como piloto/runbook.

**Tech Stack:** .NET 10, `TcpClient` puro (sem pacote novo esperado; nClam só se a avaliação mostrar manutenção ativa), EF migration raw-SQL (padrão do repo), testes públicos xUnit.

**Spec:** `docs/handoffs/agentic-mvp/priority-agent-antimalware.md` · decisão de engine: `docs/superpowers/specs/2026-08-18-issue-104-antimalware-engine-comparison.md` (ClamAV/clamd **APROVADA pelo responsável em 2026-08-19** — registrar no doc).

## Global Constraints

- Fail-closed inegociável herdado do #104/#246: sem endpoint clamd configurado, produção continua `BlockedMalwareScanner` (`scanner_not_configured`); NENHUMA liberação sem veredito Clean atual com checksum vinculado.
- O cliente nunca envia veredito/engine/versão; versões vêm do daemon (`VERSION`), nunca do cliente.
- Persistir SOMENTE `scan_engine`, `scan_engine_version`, `scan_signature_version`, `scanned_at` (nullable, sem backfill) — nas saídas duráveis; NUNCA payload/bytes/PII em log, teste, doc ou coluna.
- Transitório (timeout/scan_failed/limit_exceeded/indisponível) continua retryable em `quarantined` SEM persistir scan metadata nem mudar estado — semântica do PR #246 intocada.
- Limite de bytes (`MaxScanBytes`) imposto durante o streaming; exceder → veredito `Unavailable`/`limit_exceeded` (transitório por decisão: corrigível por config, sem evidência sobre o objeto).
- Checksum do veredito = SHA-256 computado durante o streaming para o clamd (bind veredito↔conteúdo).
- Um arquivo por classe/família coesa; net10.0; `TreatWarningsAsErrors`; lockfiles; `dotnet format whitespace --verify-no-changes`; docs PT compactas; sem pacote sem uso.
- ApiHost continua enxuto (composição condicional mínima); OCR/DeepSeek/agente API-UI/WhatsApp/`quality/private` intocados.
- Branch: `codex/issue-104b-clamav-engine` a partir de `63cda46` (base do PR #246). Worktree: `.worktrees/agent-antimalware-engine`.
- Migration: raw SQL `ALTER TABLE` (padrão do repo; ver `20260809184931_AddAuditScope` e InitialFoundationBaseline para estilo) + propriedades/config EF para o snapshot; nomes snake_case via convenção existente. Geração exige env de design-time (ver `FoundationDesignTimeDbContextFactory`; receita nos PRs #230/#231).

## Estimativa

~20 caminhos (4 Files novos/modificados, 2 Files de domínio/config, 2 migrations geradas, service, Program.cs, ~6 arquivos de teste, 3 docs) + lockfiles se pacote entrar (não esperado). ~700–900 linhas manuais. Justificativa de tamanho: fatia vertical única da engine aprovada.

---

### Task 1: Acesso a stream do objeto no FileStorage

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/FileStorage.cs` (interface)
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs` (compor `OpenContentAsync` do `MalwareScanRequest` — hoje `null`)
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseFileStorageTests.cs` (estender)

**Interfaces:**
- Produces:

```csharp
// FileStorage (novo membro):
/// <summary>Abre o conteúdo privado do objeto para leitura única; o caller dispõe o stream.</summary>
Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken);

// DocumentUploadService (composição):
var request = new MalwareScanRequest(
    providerKey,
    cancellationToken => storage.OpenReadAsync(providerKey, cancellationToken));
```

- `SupabaseFileStorage.OpenReadAsync`: GET autenticado no path do objeto com `HttpCompletionOption.ResponseHeadersRead`, `EnsureSuccessStatusCode`, `ValidateObjectKey`, e retorna `await response.Content.ReadAsStreamAsync(ct)` SEM buffer; redirects já desabilitados no cliente (não tocar). Não impõe limite aqui (o limite é do scanner, durante o streaming).

- [ ] **Step 1: RED** — casos em `SupabaseFileStorageTests` (seguir o padrão de fake HttpMessageHandler existente no arquivo):
  1. `Open_read_returns_the_content_stream_without_buffering` — handler devolve bytes; assert conteúdo lido correto e `response.Content` não totalmente materializado (fake que registre `HttpCompletionOption` recebida == `ResponseHeadersRead`).
  2. `Open_read_validates_the_object_key` — key inválida → mesma exceção do `InspectObjectAsync` (padrão `ValidateObjectKey`).
  3. `Open_read_fails_closed_on_http_error` — 404/403 → exceção (nunca stream vazio silencioso).
  4. `Malware_scan_request_carries_a_content_opener` — no service: o `MalwareScanRequest` entregue ao scanner (fake captura) tem `OpenContentAsync` não-nulo E que abre o conteúdo (invocar o delegate no fake e contar 1 abertura; `BlockedMalwareScanner` de produção continua sem abrir).
- [ ] **Step 2: Run RED** — `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` (falha de compilação: membro novo na interface quebra fakes; atualizar fakes de `FileStorage` nos testes conforme necessário — é parte do GREEN).
- [ ] **Step 3: GREEN** — implementar interface + Supabase + composição no service. TODOS os fakes/implementation de `FileStorage` (grep por `: FileStorage`) ganham a implementação.
- [ ] **Step 4: Run GREEN** — Unit Foundation verde + build limpo.
- [ ] **Step 5: Formatar e commit** — `feat(files): open private object content stream for server-side scanning`.

---

### Task 2: Cliente clamd e adapter ClamAvMalwareScanner

**Files:**
- Create: `src/Foundation/Vinx.Erp.Foundation/Files/ClamAv/ClamAvDaemonClient.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Files/ClamAv/ClamAvMalwareScanner.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/MalwareScannerOptions.cs` (`ClamAvEndpoint`)
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/ClamAv/FakeClamAvDaemon.cs` (servidor TCP in-process)
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/ClamAv/ClamAvDaemonClientTests.cs`
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/ClamAv/ClamAvMalwareScannerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class MalwareScannerOptions
{
    // ... existente (Timeout 30s, MaxScanBytes 25 MiB, Validate) ...
    /// <summary>Endpoint TCP do clamd ("host:port"). Nulo/vazio mantém a engine bloqueada (fail-closed).</summary>
    public string? ClamAvEndpoint { get; set; }
    // Validate: se presente, deve ser "host:port" com porta válida (Uri/ManualParse; ArgumentException c/ ParamName).
}

namespace Vinx.Erp.Foundation.Files.ClamAv;

/// <summary>Cliente mínimo do protocolo clamd (nUL-terminated commands; INSTREAM com chunks de 4 bytes BE).</summary>
internal sealed class ClamAvDaemonClient(string host, int port) : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task<ClamAvVersion> ReadVersionAsync(CancellationToken ct);            // zVERSION\0 → "ClamAV 1.4.2/27130/date"
    Task<ClamAvScanResult> ScanStreamAsync(Stream content, long maxBytes, CancellationToken ct);
    // zINSTREAM\0; chunks <len BE><data> de 64 KiB; terminador 0x00000000; resposta "stream: OK\0"
    // | "stream: <SIGNATURE> FOUND\0" | erro de tamanho/protocolo → resultado tipado, NUNCA exceção de protocolo.
}

internal sealed record ClamAvVersion(string EngineVersion, string SignatureVersion);
internal sealed record ClamAvScanResult(bool Clean, string? SignatureName, string? ErrorCode);

/// <summary>Adapter IMalwareScanner sobre o clamd; veredito com checksum computado durante o streaming.</summary>
public sealed class ClamAvMalwareScanner(MalwareScannerOptions options) : IMalwareScanner;
```

- Fluxo do `ScanAsync`: abrir conteúdo via `request.OpenContentAsync` (se null → `Unavailable`/`scan_failed`); conectar; `ScanStreamAsync` com hash SHA-256 incremental e checagem `MaxScanBytes` (exceder → fechar e retornar `Unavailable`/`limit_exceeded`); FOUND → `MalwareDetected` (SignatureName SÓ no log seguro? NÃO — nunca vaza: reason code permanece `malware_detected` na política; o nome da assinatura NÃO vai no veredito — `ReasonCode` seguro); Clean → `ReadVersionAsync` → veredito Clean com Engine="ClamAV", EngineVersion, SignatureVersion, `ContentChecksum="sha256:..."`, `ScannedAtUtc=UtcNow`.
- Erros: conexão/recusa/RESET → `Unavailable`/`scan_failed`; resposta inparseável → `Unavailable`/`engine_output_malformed`; `OperationCanceledException` com ct externo → rethrow (o timeout interno vira veredito pelo service — compatível com o fluxo do #246; se o cancelamento for do linked timeout, o service já mapeia `scan_timeout`).
- **Avaliação nClam (obrigatória antes de codar o cliente):** WebSearch — nClam (MIT) última release/manutenção/Sockets support. Se mantido ativamente (release < 12 meses), USAR nClam no lugar do cliente interno (justificar em Directory.Packages.props); senão, cliente interno com justificativa curta no PR (protocolo estável de ~4 comandos, sem SDK oficial, evitar dependência sem manutenção). Registar a decisão no relatório e no doc de comparação.

- [ ] **Step 1: RED — `FakeClamAvDaemon`** (TcpListener in-process; scripts programáveis: resposta VERSION fixa "ClamAV 1.4.2/27130/...", eco determinístico do protocolo INSTREAM, modos: ok/found("Eicar-Signature"?) NÃO usar nome real de assinatura sensível — usar "Test.Signature.Test"-like/timeout/garbage/refuse/huge).
- [ ] **Step 2: RED — casos `ClamAvDaemonClientTests`**: version parse; scan clean; scan found com nome; size limit do clamd (daemon responde erro de tamanho); garbage response; connection refused; chunking correto (fake valida terminador e tamanho BE; payload multi-chunk).
- [ ] **Step 3: RED — casos `ClamAvMalwareScannerTests`**:
  1. `Clean_scan_returns_verdict_with_versions_and_content_checksum` — checksum confere com SHA-256 do conteúdo enviado.
  2. `Detected_scan_returns_malware_detected_without_signature_leak` — Status Detected; ReasonCode seguro; veredito NÃO expõe SignatureName (allowlist de propriedades já cobre).
  3. `Byte_limit_exceeded_returns_limit_exceeded_unavailable` — conteúdo > MaxScanBytes (options de teste) → transitório; daemon recebe abort (fake registra desconexão).
  4. `Daemon_down_returns_scan_failed` (porta fechada).
  5. `Malformed_daemon_output_returns_engine_output_malformed`.
  6. `External_cancellation_propagates`.
  7. `Missing_content_opener_returns_scan_failed` (request sem delegate).
  8. `Options_endpoint_validation` — null OK; "host:port" OK; "host", ":700", "host:notaport" → ArgumentException.
  9. `Content_is_read_exactly_once` (contador de aberturas == 1).
- [ ] **Step 4: Run RED** → compilação/afirmações falham.
- [ ] **Step 5: GREEN** — implementar cliente + scanner + options. Sem `using` de pacote novo se nClam reprovado.
- [ ] **Step 6: Run GREEN** + build limpo.
- [ ] **Step 7: Formatar e commit** — `feat(files): clamd INSTREAM malware scanner adapter`.

---

### Task 3: Persistência dos metadados de scan

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Domain/FileRecord.cs` (4 propriedades + `RecordScan(engine, engineVersion, signatureVersion, scannedAtUtc)`)
- Modify: `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/FoundationEntityConfigurations.cs` (FileRecordConfiguration)
- Create: `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260819XXXXXX_AddFileScanMetadata.cs` (+ `.Designer.cs` gerado)
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs` (persistir na liberação clean e no terminal `malware_detected`)
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/FileRecordScanMetadataTests.cs`
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs` (estender)

**Interfaces:**
- Produces: `FileRecord` ganha `ScanEngine/ScanEngineVersion/ScanSignatureVersion/ScannedAtUtc` (nullable, `<summary>` documentando bind ao veredito) e `RecordScan(...)` chamado APENAS nas saídas duráveis (o terminal de mismatchs de metadados NÃO grava — não houve scan; só clean-confirmado e `malware_detected`).
- Migration: `ALTER TABLE foundation.files ADD COLUMN scan_engine character varying(100), ADD COLUMN scan_engine_version character varying(50), ADD COLUMN scan_signature_version character varying(50), ADD COLUMN scanned_at timestamp with time zone;` (todos nullable; sem backfill; ver padrões de migração raw existentes). Geração: seguir `FoundationDesignTimeDbContextFactory` + receita de env dos PRs #230/#231 (FOUNDATION_MIGRATOR_CONNECTION + FOUNDATION_RUNTIME_CONNECTION + SUPABASE_AUTH_* dummies de formato válido); o arquivo gerado DEVE ser revisado para conter somente o ALTER pretendido.
- Service: no caminho de sucesso (clean confirmado) e no terminal `malware_detected`, `file.RecordScan(verdict.Engine!, verdict.EngineVersion!, verdict.SignatureVersion!, verdict.ScannedAtUtc)` antes do SaveChanges; transitório/mismatch NÃO grava (scans ficam NULL — evidência de que não houve veredito durável).

- [ ] **Step 1: RED — unit `FileRecordScanMetadataTests`**: `RecordScan_populates_metadata_only_via_method` (propriedades private set; chamada popula); `RecordScan_requires_complete_metadata` (string vazia/null/scannedAt default → ArgumentException); estado não muda (`ProcessingState` inalterado após RecordScan).
- [ ] **Step 2: RED — integração** (estender DocumentUploadEndpointTests com fake clamd via factory):
  1. `Clean_confirmation_persists_scan_metadata` — fake clamd clean → confirmed; row com engine="ClamAV", versões e scanned_at não-nulos.
  2. `Malware_detected_persists_scan_metadata_and_stays_terminal` — fake FOUND → verification_failed + metadados preenchidos.
  3. `Transient_unavailable_keeps_scan_metadata_empty` — fake fora do ar (scan_failed) → quarantined retryable + colunas NULL.
  4. `Metadata_mismatch_terminal_keeps_scan_metadata_empty` (não houve scan).
- [ ] **Step 3: Run RED**.
- [ ] **Step 4: GREEN** — entidade + config + migration (gerar com env de design-time; revisar diff do arquivo) + service. `dotnet build` limpo.
- [ ] **Step 5: Run GREEN** — unit + integração (filtro DocumentUpload) verdes; a migration nova é aplicada pela fixture Testcontainers automaticamente.
- [ ] **Step 6: Formatar e commit** — `feat(files): persist malware scan engine metadata on durable outcomes`.

---

### Task 4: Composição condicional no host e documentação

**Files:**
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs` (se `ClamAvEndpoint` presente → `ClamAvMalwareScanner`, senão `BlockedMalwareScanner`; validação on-start existente cobre o formato)
- Modify: `docs/superpowers/specs/2026-08-18-issue-104-antimalware-engine-comparison.md` (Status: APROVADA ClamAV/clamd em 2026-08-19 pelo responsável; registro da decisão nClam vs cliente interno; nota de operação: imagem `clamav/clamav` com clamd exposto em TCP, `freshclam` diário, monitor de versão de assinatura)
- Modify: `docs/modules/foundation.md` (engine real condicionada por config; sem endpoint → bloqueio)
- Modify: `docs/verification/issue-104-antimalware-test-cases.md` (novos casos; atualizar lacunas: persistência ENTREGUE nesta PR; re-scan assíncrono/E2E real clamd continuam pendentes)
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs` (se necessário ao override)

- [ ] **Step 1: RED** — teste de composição: produção SEM endpoint resolve `BlockedMalwareScanner` (já coberto); NOVO: `Host_registers_clamav_scanner_when_endpoint_configured` (WebApplicationFactory com config `MalwareScanner:ClamAvEndpoint=localhost:port` + fake clamd naquele port → confirmação clean end-to-end confirma; sem fake em pé → scan_failed retryable). Se o custo do teste de host for desproporcional, cobrir a seleção por unidade do método de composição (extrair `AddMalwareScanner(IServiceCollection, IConfiguration?)` em Files/ — uma classe, um arquivo) e testar O MÉTODO com ServiceProvider — honesto e barato; registrar a escolha no relatório.
- [ ] **Step 2: GREEN** — composição condicional (extension method em `src/Hosts/Vinx.Erp.ApiHost/` seguindo o padrão de feature-extensions do repo, ou inline mínimo se Program.cs já registra o service diretamente — seguir o estilo vigente).
- [ ] **Step 3: Docs** — comparison (APROVADA + decisão nClam), foundation.md, verification doc.
- [ ] **Step 4: Verificações finais** — `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` + `--filter DocumentUpload` da integração + ArchitectureTests + `dotnet format whitespace --verify-no-changes` + `git diff --check`.
- [ ] **Step 5: Commit** — `feat(files): wire clamd scanner by configuration with engine docs`.

---

## Verificação final (freeze — serializado)

1. `./scripts/verify-public.sh` EXIT:0 no HEAD exato (protocolo de flake: solo + cooldown; a fixture aplica a migration nova).
2. Revisão final de branch inteira: fail-closed em toda parte, sem vazamento de assinatura/payload, transitório intacto, migration revisada (somente o ALTER pretendido), sem pacote sem uso.
3. `git diff --check`.
4. PR draft com base em `codex/issue-104-antimalware` (PR #246), evidência completa e nota de piloto com clamd real pendente.
