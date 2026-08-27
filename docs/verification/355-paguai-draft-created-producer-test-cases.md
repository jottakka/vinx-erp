# Issue #355 — produtor de rascunho criado Pagaí

| Classe | Cenário | Teste público | Situação |
| --- | --- | --- | --- |
| Sucesso e privacidade | Criação autorizada confirma rascunho, recibo, auditoria e um envelope com somente `CompanyId`, `DraftId` e `RequesterUserId`; valores financeiros, beneficiário, texto, arquivo, URL e PII não entram no envelope. | `PaguaiDraftCreatedTests.Created_draft_publishes_one_opaque_event_and_replay_keeps_one_envelope` | Automatizado |
| Erro transacional | Falha ao persistir o outbox retorna indisponível sem rascunho, recibo, auditoria ou envelope parcial; após restaurar o storage, a mesma request cria normalmente. | `PaguaiDraftCreatedTests.Outbox_persistence_failure_rolls_back_draft_receipt_audit_and_envelope_then_retry_recovers` | Automatizado |
| Acesso e tenant | O worker consulta o diretório restrito no dispatch, notifica somente aprovadores explícitos da empresa e exclui solicitante e aprovador de outra empresa. | `PaguaiDraftCreatedTests.Worker_resolves_current_company_approvers_excludes_requester_and_deduplicates_redelivery` | Automatizado |
| Estado e exceção | Template in-app ausente/reprovado ou evento com identificador vazio não cria intent; registra `Warning` seguro e não reverte o rascunho já confirmado. | `PaguaiDraftCreatedTests.Missing_template_is_best_effort_warning_without_reverting_the_draft` | Automatizado |
| Concorrência e replay | Replay da criação mantém um envelope; redelivery do mesmo evento mantém uma única intent por aprovador pela chave `paguai.<draftId:N>.draft-created`. | `PaguaiDraftCreatedTests.Created_draft_publishes_one_opaque_event_and_replay_keeps_one_envelope`; `PaguaiDraftCreatedTests.Worker_resolves_current_company_approvers_excludes_requester_and_deduplicates_redelivery` | Automatizado |
| Queue e grants | Deploy cria a fila e scheduled table `paguai-events`, concede DML somente ao worker e concede a função estreita de intent sem acesso direto às tabelas de negócio. | `WolverineStorageScriptTests.Setup_script_applies_wolverine_storage_and_runtime_grants`; `PaguaiDraftApproverDirectoryTests.Capability_and_directory_have_the_canonical_least_privilege_contract` | Automatizado |

Fora do escopo: comprovante/pagamento, provider externo, UI, broker genérico, retry próprio, grant automático de administrador e backfill.
