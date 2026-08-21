# #272 PR2 — WhatsApp inbound: documento seguro (download + quarentena)

Manifesto de casos de teste da fatia PR2 (branch empilhada no PR1/#273).
Todos públicos, PostgreSQL real via Testcontainers; nenhum caso persiste
telefone, legenda, nome de arquivo ou conteúdo em log/auditoria/DLQ.

## Sucesso

| Caso | Teste |
| --- | --- |
| Documento admitido (PDF válido) é baixado, validado, escaneado limpo e armazenado em quarentena com checksum/páginas/veredito e registro durável | `MetaWhatsAppDocumentInboundTests.Admitted_pdf_is_quarantined_with_checksum_pages_and_clean_scan` |
| Replay do webhook processa o documento uma única vez (uma linha, um upload) | `MetaWhatsAppDocumentInboundTests.Webhook_replay_processes_the_document_only_once` |

## Validação e rejeição fechada

| Caso | Teste |
| --- | --- |
| MIME fora da allowlist → `document_rejected` sem upload nem registro | `...Disallowed_media_type_is_rejected_without_quarantine` |
| PDF malformado → `document_rejected` | `...Malformed_pdf_is_rejected_without_quarantine` |
| Malware detectado → `document_rejected` sem upload | `...Malware_detected_fails_closed_without_upload` |
| Scanner indisponível/não configurado → `document_scan_unavailable` | `...Unavailable_scanner_fails_closed` |
| Storage não configurado → `document_storage_unavailable` | `...Unconfigured_storage_fails_closed` |

## Acesso e tenant

| Caso | Teste |
| --- | --- |
| Capability `foundation.files.upload` ausente no processamento → `capability_missing` sem documento | `...Missing_capability_fails_closed_without_document` |
| Privilégios fechados: worker executa claim/complete do documento; runtime/worker sem acesso direto à tabela | `...Document_function_privileges_are_closed` |

## Limites aprovados nesta fatia (proposta revisável)

- Bytes: 25 MiB (`DocumentUploadPolicy.MaxSizeBytes`, consistente com upload da UI).
- MIME: `application/pdf`, `image/jpeg`, `image/png` (subsete do upload; `text/plain` fora do corte de fatura).
- Páginas (PDF): 20 (`DocumentContextLimits.MaxPages`).
- Retenção: `retention_until` permanece sem política até a decisão pendente do #272.

## Fora de escopo (PR3/PR4)

OCR/extração tipada, candidato de rascunho, integração Pagaí, resposta
automática por WhatsApp.
