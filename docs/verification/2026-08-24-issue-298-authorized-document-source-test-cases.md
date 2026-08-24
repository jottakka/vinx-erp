# Casos de teste — 134-A/#298: fonte autorizada real de documentos

Data: 2026-08-24. Branch: `codex/issue-134a-authorized-document-source`.
Plano: `docs/superpowers/plans/2026-08-24-agent-attachments-2a-2b-2c.md`
(rev. 2, Sol APPROVED). Escopo: fronteira de leitura #134-A sobre
FileRecord/Foundation; sem mudanças de API HTTP/UI.

## Cinco classes BDD

| Classe BDD | Cenário | Resultado exigido | Evidência |
|---|---|---|---|
| sucesso | Documento `confirmed` COM veredito durável completo abre stream seekable limitado (`SizeBytes` exato). | Conteúdo íntegro; metadata/open 1/1; allow `AuditSource.Ai`, action canônica e metadata `{}`. | INT `Confirmed_document_with_durable_scan_opens_a_seekable_limited_stream_and_audits_allow` |
| validação/erros | `SizeBytes` 0 ou > 25 MiB. | `document_size_out_of_policy`; metadata/open 0/0. | INT `Record_size_outside_the_upload_policy_denies_before_any_allocation` (2 casos) |
| validação/erros | Checksum/size diverge; stream maior ou menor; metadata falha. | `checksum_mismatch`, `object_size_divergence` ou `storage_unavailable`; contadores distinguem metadata de abertura. | INT `Storage_checksum_mismatch…` / `Object_larger…` / `Object_shorter…` / `Storage_metadata_failure…` |
| acesso | Empresa errada, membership ausente/revogada ou sem grant Company `foundation.files.read-ocr`. | Reasons estáveis; nenhuma leitura de storage; empresa estrangeira invisível. | INT `Other_company_document_is_invisible…` / `Revoked_membership_denies…` / `Missing_read_capability_denies…` |
| estado/exceções | `confirmed` sem scan completo, `quarantined`, `verification_failed`, soft-delete ou retenção vencida. | `document_not_released`, `resource_not_found` ou `retention_expired`; metadata/open 0/0. | INT `Confirmed_row_without_durable_scan…` / `Quarantined_verification_failed_deleted_and_retention_expired_documents_deny` |
| estado/exceções | Cancelamento explícito durante leitura. | `OperationCanceledException` propaga; nenhum allow/deny audit é inventado. | INT `Caller_cancellation_during_storage_read_propagates_without_audit` |
| estado/exceções | Auditoria allow ou deny indisponível. | `audit_unavailable`; conteúdo não é liberado; exatamente um Error log com código estável. | INT `Failed_allow_audit…` / `Failed_denial_audit…` |
| concorrência | Membership revogada enquanto o storage está bloqueado. | Reautorização final nega `membership_required`, audita deny e não audita allow. | INT `Membership_revoked_while_storage_is_read_denies_before_content_is_released` |
| concorrência | Checksum autoritativo muda enquanto o storage está bloqueado. | Tupla final diverge: `document_changed`, deny auditado, sem allow. | INT `File_tuple_changed_while_storage_is_read_invalidates_the_buffered_object` |
| concorrência | Revogação entre chamadas completas. | Primeira leitura allow; chamada seguinte reautoriza e nega. | INT `Revoked_membership_denies_on_the_next_read` |

## Regras cruzadas exercitadas

- Assinatura explícita `OpenReadAsync(actor, companyId, fileRecordId, ct)`
  (ruling Sol (b)) — ator/empresa nunca vêm do request do cliente.
- Interface + normalizer + fakes #134 atualizados mecanicamente (mesma
  semântica); os consumidores permanecem cobertos pelas suítes AI.
- Capability `foundation.files.read-ocr` seedada (migration
  `AddFilesReadOcrCapability`, id UUIDv5 `f9df3888-…`), grant default-deny e
  downgrade específico falha quando essa capability está referenciada.
- Preflight e decisão final usam `RepeatableRead`; deny do preflight/final e
  allow final confirmam auditoria na mesma transação. Auditoria carrega apenas
  IDs opacos, reason estável e `{}` — nunca conteúdo, checksum, chave ou PII.

## Suítes

- INT focado: classe `AuthorizedDocumentSourceTests` (Postgres/Testcontainers,
  um fixture por vez).
- Migration focada: classe `CapabilityCatalogMigrationTests`, incluindo
  `Files_read_ocr_downgrade_fails_closed_when_the_capability_is_referenced`.
- Consumidores: projetos `Vinx.Erp.AI.UnitTests` e
  `Vinx.Erp.AI.IntegrationTests`.
- Resultado e totais da execução atual pertencem à evidência da PR; o CI é o
  gate público completo. Este manifesto não copia logs transitórios.
