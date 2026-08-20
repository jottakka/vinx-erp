# Casos testados — Paguai rascunho financeiro mínimo

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Dado payload financeiro válido, quando cria, então retorna `201` com todos os campos e `rascunho` | `PaguaiDraftEndpointTests.Draft_creation_returns_a_company_scoped_rascunho` | Detecta perda de campo ou estado incorreto | coberto |
| Dado `amountMinor` zero ou negativo, quando cria, então retorna `400` | `PaguaiDraftEndpointTests.Draft_creation_rejects_non_positive_amount` | Detecta valor não positivo | coberto |
| Dado moeda, vencimento ou exigência inválidos, quando cria, então retorna `400` | `PaguaiDraftEndpointTests.Draft_creation_rejects_invalid_currency_due_date_and_proof_requirement` | Detecta allowlist e parser frouxos | coberto |
| Dado empresa diferente, quando lista/detalha, então não vê o rascunho | `PaguaiDraftEndpointTests.Draft_reads_are_visible_to_member_company_only`; `PaguaiDraftSchemaTests.Runtime_rls_fails_closed_without_context_and_hides_another_requester` | Detecta vazamento entre empresas | coberto |
| Dado mesmo `requestId` e payload, quando repete, então retorna replay sem duplicar; payload diferente retorna `409` | `PaguaiDraftEndpointTests.Replaying_same_payload_returns_same_draft_and_different_payload_conflicts`; `Concurrent_identical_draft_creates_return_one_creation_one_replay_and_one_audit` | Detecta hash incompleto e corrida | coberto |
| Dado auditoria indisponível, quando cria, então não persiste rascunho nem idempotência | `PaguaiDraftEndpointTests.Audit_failure_rolls_back_draft_and_idempotency_then_the_same_request_can_retry` | Detecta quebra da garantia transacional | coberto |
| Dado rascunho criado, então não há notificação, worker, outbox ou mudança de estado | Contrato do módulo e auditoria `{}` | Mantém o corte em criação/lista/detalhe | coberto por escopo; sem notificação é N/A — nenhum componente é tocado |
