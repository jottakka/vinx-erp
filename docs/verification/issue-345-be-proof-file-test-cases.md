# Casos testados — issue #345 backend: comprovante do rascunho Pagaí (MVP documentation-only, corte create+read+link)

Matriz BDD do contrato congelado (`/tmp/vinx-345-mvp-decision.md`, seção C) mapeada para os testes reais.
Comprovante é documentação anexada a rascunho: nenhuma operação do corte cria estado financeiro, evento de pagamento ou notificação.

Escopo deste PR (#357): criação com comprovante opcional, leitura segura e link assinado de curta duração.
A troca versionada via `PATCH /paguai/requests/{id}/proof` (CAS em `expectedVersion`), o relaxamento
`CHECK (version >= 1)` e o `GRANT UPDATE (version)` seguem no PR empilhado `codex/issue-345-proof-cas`,
criado a partir deste; a matriz abaixo cobre apenas o que existe neste corte.

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso — create: requerente com membership ativo + `paguai.draft.create` + arquivo vivo confirmado, mesma empresa/uploader, `paguai_request_proof`; cria com `proofFileId` | `PaguaiProofEndpointTests.Create_with_proof_returns_created_draft_with_safe_metadata_and_single_audit` | `201`; estado `rascunho`, versão `1`; ponteiro + metadados seguros sobrevivem leitura fresca; auditoria única `paguai.draft.create` com razão `proof_attached`; nenhum evento além do padrão `PaguaiDraftCreated` | coberto |
| Sucesso — independência requisito/anexo: `required` sem prova ou `optional` com prova | `PaguaiProofEndpointTests.Requirement_and_attachment_remain_independent` | Ambas aceitas; requisito e anexo seguem independentes | coberto |
| Sucesso — view: detalhe expõe só metadados seguros; link fresco `no-store`, expira em até 5 min e nunca após `retention_until` | `PaguaiProofEndpointTests.Detail_and_list_expose_only_safe_proof_metadata`; `Proof_link_is_freshly_signed_no_store_and_expires_within_five_minutes`; `Proof_link_ttl_is_capped_by_retention_deadline` | Sem chave do provedor/checksum/retenção/URL no payload; TTL exato de 5 min e teto pela retenção; resposta `private, no-store` | coberto |
| Validação — corpo: `proofFileId`/empresa vazio, membro JSON desconhecido, campo obrigatório ausente | `PaguaiProofEndpointTests.Invalid_create_bodies_are_rejected_without_mutation_or_audit` | `400 invalid_request`; sem mutação, sem auditoria de sucesso | coberto |
| Validação — ciclo de vida: ausente, outra empresa, outro uploader, classificação errada, removido, retenção vencida | `PaguaiProofEndpointTests.Bind_lifecycle_failures_are_typed_and_audited_without_file_metadata` | `404 proof_file_not_found`; negação auditada com razão controlada e `metadataJson` `{}` sem UUID do arquivo | coberto |
| Validação — ciclo de vida: classe correta ainda não confirmada (quarentena) | idem (`quarantined`) | `400 proof_file_not_ready` (única família 400 do vínculo) | coberto |
| Validação — link: prova venceu/foi removida após o vínculo, ou rascunho sem prova | `PaguaiProofEndpointTests.Link_lifecycle_fails_closed_after_expiry_or_deletion_or_without_proof` | `404 resource_not_found`; assinador NÃO chamado; nenhuma URL produzida | coberto |
| Validação — dependência: auditoria falha durante o vínculo; storage falha ao assinar | `PaguaiProofEndpointTests.Audit_failure_rolls_back_the_proof_bound_create_and_its_audit`; `Storage_signing_failure_returns_unavailable_without_altering_the_draft` | Mutação e auditoria de sucesso não se separam (`503 paguai_unavailable`); criação revertida; rascunho intacto | coberto |
| Acesso — autenticação/capability: não autenticado; sem `paguai.draft.create`; sem `paguai.draft.read` | `PaguaiProofEndpointTests.Unauthenticated_calls_are_rejected`; `Capability_denials_are_typed_for_create_and_fail_closed_for_reads` | `401`; criação negada `403 capability_denied` auditada; leitura fail-closed `404 resource_not_found` sem mutação | coberto |
| Acesso — requerente/tenant: outro requerente mira rascunho alheio em leitura/link | `PaguaiProofEndpointTests.Foreign_requester_drafts_are_hidden_from_read_and_link` | RLS + predicado requerente ocultam o recurso (`404 resource_not_found`); versão/ponteiro intactos; zero assinaturas | coberto |
| Estado: prova ausente/anexada/indisponível/exigida apenas por `proofRequirement` | asserts de estado `rascunho` embutidos em todos os casos acima | Estado permanece exatamente `rascunho`; prova não confirma nem bloqueia (cenários terminais N/A no schema atual) | coberto |
| Concorrência — replay de criação: mesmo `requestId`/payload (inclusive após o arquivo vencer); proof diferente sob a mesma chave | `PaguaiProofEndpointTests.Replay_after_expiry_returns_original_draft_without_second_bind_or_audit`; `Replay_with_a_different_proof_returns_idempotency_conflict` | Replay `200` do rascunho original sem segundo vínculo/auditoria; comprovante distinto `409 idempotency_conflict` | coberto |
| Concorrência — prova única: dois rascunhos disputam o mesmo arquivo | `PaguaiProofEndpointTests.Unique_proof_race_is_typed_conflict_from_the_partial_unique_index` | Índice parcial único rejeita em storage (`23505`/`uq_paguai_requests_proof_file`); perdedor da API recebe `409 proof_file_bound` tipado, nunca `503`, sem mutação parcial | coberto |
| Banco — grant coluna-nível: runtime atualiza só `proof_file_id` | `PaguaiDraftMigrationTests.Proof_column_grant_is_column_level_only_for_runtime` | `has_column_privilege` verdadeiro apenas para `proof_file_id`; falso para `version`, `amount` e `beneficiary_display_name` (o grant de `version` chega no PR empilhado) | coberto |
| Banco — Down fail-closed: prova vinculada | `PaguaiDraftMigrationTests.Proof_downgrade_fails_closed_while_a_proof_is_bound` | Downgrade recusa (`23514`) preservando coluna, índice, linha de migration e dados | coberto |
| Banco — Down vazio: remove coluna, revoga grant | `PaguaiDraftMigrationTests.Empty_proof_downgrade_removes_column_and_keeps_the_fixed_version_check` | Coluna removida; grant de UPDATE revogado; `CHECK (version = 1)` segue intacto (insert com `version = 2` permanece impossível) | coberto |

Fora deste corte (PR empilhado `codex/issue-345-proof-cas`): PATCH/troca CAS, `proof_version_conflict`,
auditoria `paguai.draft.replace_proof`, `CHECK (version >= 1)`, `GRANT UPDATE (version)` e antiforgery de PATCH.

Regressão sem quebra: `PaguaiDraftCreatedTests`, `PaguaiProposalEndpointTests`, `PaguaiDraftMigrationTests` (cenários pré-existentes) e `PaguaiInboundDocumentDraftTests` seguem verdes com o contrato estendido (hash byte-idêntico sem comprovante).
