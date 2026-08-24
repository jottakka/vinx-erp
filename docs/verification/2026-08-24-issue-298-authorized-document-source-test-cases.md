# Casos de teste — 134-A/#298: fonte autorizada real de documentos

Data: 2026-08-24. Branch: `codex/issue-134a-authorized-document-source`.
Plano: `docs/superpowers/plans/2026-08-24-agent-attachments-2a-2b-2c.md`
(rev. 2, Sol APPROVED). Escopo: fronteira de leitura #134-A sobre
FileRecord/Foundation; sem mudanças de API HTTP/UI.

## Cinco classes BDD

| Classe BDD | Cenário | Resultado exigido | Evidência |
|---|---|---|---|
| sucesso | Documento `confirmed` COM veredito durável completo abre stream seekable limitado (`SizeBytes` exato) e audita allow (`foundation.files.read-ocr`, metadata "{}"). | `AuthorizedDocument` com conteúdo íntegro; 1 abertura de storage. | INT `Confirmed_document_with_durable_scan_opens_a_seekable_limited_stream_and_audits_allow` |
| validação | Registro fora da política de tamanho (0 ou > 25 MiB) nega `document_size_out_of_policy` ANTES de qualquer alocação/abertura. | Zero chamadas a `OpenReadAsync` do storage. | INT `Record_size_outside_the_upload_policy_denies_before_any_allocation` |
| validação | Checksum/size do storage divergem do registro → `checksum_mismatch` SEM abrir; objeto maior que o registrado (byte de guarda) → `object_size_divergence`; metadata indisponível → `storage_unavailable` fail-closed. | Negações auditadas sem conteúdo; abertura zero nos dois primeiros. | INT `Storage_checksum_mismatch…` / `Object_larger_than_the_registered_size…` / `Storage_metadata_failure…` |
| acesso | Documento de outra empresa é invisível (`resource_not_found`); membership revogada nega `membership_required` na leitura seguinte; sem grant da capability `foundation.files.read-ocr` nega `capability_denied` (default deny). | Zero aberturas de storage; empresa errada nunca vaza conteúdo. | INT `Other_company_document_is_invisible…` / `Revoked_membership_denies…` / `Missing_read_capability_denies…` |
| estado | `confirmed` SEM metadados de scan duráveis nega `document_not_released` sem tocar storage (finding 2 do Sol); `quarantined` nega; soft-deleted vira `resource_not_found`; retenção vencida nega `retention_expired`. | Nenhuma abertura de storage; reasons estáveis. | INT `Confirmed_row_without_durable_scan…` / `Quarantined_deleted_and_retention_expired_documents_deny` |
| concorrência | Leitura re-autoriza a cada chamada (revogação entre duas leituras: 1ª allow, 2ª deny) — sem cache/estado. | Desfechos independentes. | INT `Revoked_membership_denies_on_the_next_read` |

## Regras cruzadas exercitadas

- Assinatura explícita `OpenReadAsync(actor, companyId, fileRecordId, ct)`
  (ruling Sol (b)) — ator/empresa nunca vêm do request do cliente.
- Interface + normalizer + fakes #134 atualizados mecanicamente (mesma
  semântica); suíte #134 verde (AI.UnitTests 300/300).
- Capability `foundation.files.read-ocr` seedada (migration
  `AddFilesReadOcrCapability`, id UUIDv5 `f9df3888-…`), catalog test 4/4;
  grant default-deny (sem RoleCapability não lê).
- Auditoria allow E deny (`AuditSource.Ai`) com metadata "{}" — sem
  conteúdo, checksum, chave ou PII.

## Suítes

- INT = `Vinx.Erp.Foundation.IntegrationTests.AuthorizedDocumentSourceTests`
  (Postgres/Testcontainers): 10/10 verdes solo (2 runs: 1º 9/10 por bug de
  seed do próprio teste — FK de usuário estrangeiro — corrigido; 2º 10/10).
- `CapabilityCatalogMigrationTests` 4/4 verdes solo.
- `Vinx.Erp.AI.UnitTests` 300/300 (assinatura nova nos fakes).
- Gate `./scripts/verify-public.sh` no HEAD exato: ver comentário do PR.
