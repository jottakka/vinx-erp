# Casos BDD — issue 227, Paguai MVP de rascunho

| Classe BDD | Dado / quando / então | Evidência | Resultado esperado |
| --- | --- | --- | --- |
| criação | Dado membership e capability de criação, quando envia metadados válidos, então cria o rascunho | `PaguaiDraftEndpointTests.Draft_creation_returns_a_company_scoped_rascunho` | `201`, `Location`, `rascunho`, versão `1` |
| limite HTTP | Dado JSON válido com mídia incorreta ou corpo grande, quando tenta criar, então o host recusa antes da mutação | `PaguaiDraftEndpointTests.Draft_creation_rejects_a_non_json_content_type_before_any_mutation`; `Draft_creation_rejects_a_body_larger_than_the_explicit_http_limit_before_any_mutation` | mídia incorreta `415`; limite `400 {"code":"invalid_request"}`; sem solicitação ou recibo |
| idempotência | Dado o mesmo `requestId`, quando repete o mesmo corpo ou muda o título, então reobtém ou conflita | `PaguaiDraftEndpointTests.Replaying_same_payload_returns_same_draft_and_different_payload_conflicts` | `200` mesmo ID, sem duplicata; `409` no hash diferente |
| visibilidade | Dado rascunho próprio, quando lista/detalha na empresa autorizada ou em empresa externa, então aplica escopo | `PaguaiDraftEndpointTests.Draft_reads_are_visible_to_member_company_only` | leitura própria `200`; detalhe externo `404`; lista externa `403` sem conteúdo |
| schema/RLS/grants | Dado banco migrado, quando runtime acessa `paguai.requests` sem contexto ou tenta mutação indevida, então o banco nega | `PaguaiDraftSchemaTests.Draft_metadata_schema_forces_requester_rls_and_grants_runtime_only_read_and_insert` | RLS forçado; somente `USAGE`, `SELECT`, `INSERT`; sem acesso PUBLIC |
| concorrência | Dadas duas criações simultâneas com o mesmo corpo, quando uma vence a chave idempotente, então a outra reconcilia após autorizar | `PaguaiDraftEndpointTests.Concurrent_identical_draft_creates_return_one_creation_one_replay_and_one_audit` | uma criação e um replay, uma só auditoria de sucesso |
| auditoria | Dada criação e replay, quando consulta a auditoria, então há somente o sucesso seguro; dada falha do appender, então toda a transação reverte | `PaguaiDraftEndpointTests.Draft_creation_appends_exactly_one_safe_success_audit_event_and_replay_does_not_append_another`; `Audit_failure_rolls_back_draft_and_idempotency_then_the_same_request_can_retry` | metadados `{}` sem motivo; sem solicitação/recibo após falha; retry válido |

Transições não têm endpoint neste MVP; concorrência de transição fica fora de
escopo deliberadamente. Verificação manual da UI: abrir `/paguai` com sessão real; o seletor oferece
somente empresas com capability Paguai, a lista vem da API same-origin, e a
criação mantém o `requestId` em caso de falha ambígua para retry explícito.
