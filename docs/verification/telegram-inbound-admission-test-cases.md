# Casos BDD — admissão de texto Telegram

## Escopo

Matriz direta de PostgreSQL da Task 3.2. Este corte não cria endpoint,
publicação, fila ou worker.

| Classe BDD | Dado / quando / então | Evidência automatizada |
| --- | --- | --- |
| Sucesso | Dado dois chats privados validados, cada um com uma única membership ativa, quando resolver e admitir, então cada linha `pending` guarda somente a identidade do seu tenant e retorna `created`. | `Validated_private_chats_resolve_and_admit_only_their_own_tenant_identity` |
| Validação e erros | Dado IDs nulos, zero, negativos ou fora do limite, texto em branco, 4097 caracteres ou tupla de resolução inválida, quando admitir, então a função retorna SQLSTATE `22023`; 4096 é aceito. | `Admission_rejects_invalid_ids_text_limits_and_resolution_tuples` |
| Acesso | Dado ausência de contato, contato não validado, membership revogada, empresa excluída ou duas identidades ativas, quando resolver, então retorna estado fechado sem IDs; runtime só executa as duas funções e não lê a tabela. | `Resolver_executes_every_closed_branch_without_exposing_tenant_ids`; `Runtime_has_only_the_two_secure_definer_functions_and_no_direct_inbound_table_access` |
| Estado e exceções | Dado uma primeira admissão fechada, validação posterior do contato, replay exato e reutilização incompatível, quando admitir, então retorna `created`, `replay` e `conflict` sem alterar a primeira evidência. Rollback não deixa linha parcial. | `Exact_replay_and_conflict_preserve_the_first_closed_evidence_after_later_validation`; `Rolled_back_admission_leaves_no_evidence_and_allows_a_new_created_result` |
| Concorrência e migração | Dadas duas admissões simultâneas da mesma chave, então uma cria e outra reproduz sem `UNIQUE`; upgrade cria o contrato, downgrade vazio o remove e downgrade com evidência falha fechada. | `Concurrent_duplicate_admission_returns_created_and_replay_without_a_unique_violation`; `Migration_up_and_empty_down_create_then_remove_only_the_inbound_admission_contract`; `Migration_down_refuses_to_discard_durable_inbound_evidence` |
