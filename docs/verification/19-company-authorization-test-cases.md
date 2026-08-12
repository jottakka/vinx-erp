# Casos públicos — issue 19, autorização por empresa

## Escopo desta fatia

Esta fatia introduz a avaliação de capability por membership, o catálogo
durável de Foundation, o mapa autenticado e a revogação interna de membership.
Não há rota CRUD, MCP, UI, grant novo ou alteração de RLS nesta entrega.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| catálogo | Dado banco novo, quando aplica migrations, então cada capability canônica existe uma vez com UUID estável; reaplicar não duplica e runtime somente lê | `CapabilityCatalogMigrationTests.Migration_seeds_each_known_capability_once_without_runtime_mutation_privileges` | coberto |
| catálogo legado | Dado nome canônico pré-existente com UUID legado, quando aplica migration, então preserva sua linha; UUID canônico ocupado por nome diferente falha | `CapabilityCatalogMigrationTests.Migration_preserves_the_existing_id_for_a_known_name_when_downgraded`; `CapabilityCatalogMigrationTests.Migration_fails_explicitly_when_a_known_id_belongs_to_a_different_name` | coberto |
| escopo | Dado grant `self`, `team`, `unit` ou `group`, quando executa comando de empresa, então nega; somente `company` satisfaz | `CapabilityEvaluatorIntegrationTests.Company_command_requires_an_exact_company_grant` | coberto |
| Alfa/Beta | Dado papel, grant ou assignment de outra empresa, quando avalia Alfa, então Beta não autoriza | `CapabilityEvaluatorIntegrationTests.Another_company_assignment_and_grant_never_authorize_the_requested_company` | coberto |
| auditoria | Dado ator sem grant efetivo, quando tenta atribuir papel ou enviar/confirmar documento, então nega com auditoria e sem mutação | `RoleAssignmentServiceIntegrationTests.Restricted_scope_does_not_authorize_a_company_role_assignment`; `DocumentUploadEndpointTests.Restricted_scopes_do_not_authorize_company_upload_commands` | coberto |
| concorrência | Dadas escritas concorrentes de atribuição, quando disputa a mesma operação, então idempotência e integridade preservam resultado seguro | `RoleAssignmentServiceIntegrationTests.Concurrent_assignment_retries_return_the_same_stored_result_without_audit_or_assignment_duplicates` | coberto |
| downgrade | Dada capability seeded referenciada, quando faz downgrade, então falha fechada sem remover catálogo | `CapabilityCatalogMigrationTests.Downgrade_fails_closed_when_a_seeded_capability_is_referenced` | coberto |
| mapa autenticado | Dado ator com memberships Alfa, Beta e Gama, quando consulta `/foundation/memberships`, então recebe somente suas memberships em ordem de nome, grants isolados por empresa e lista vazia quando não há grant | `MembershipMapEndpointTests.Membership_map_returns_only_the_current_actor_memberships_with_company_scoped_grants`; cenário BDD `Current actor membership map contains only company-scoped grants` | coberto |
| mapa anônimo | Dada chamada sem ator autenticado, quando consulta `/foundation/memberships`, então recebe `401` | `MembershipMapEndpointTests.Membership_map_rejects_an_anonymous_call` | coberto |
| revogação | Dado ator ativo com `foundation.memberships.revoke` em `company`, quando revoga membership ativa da mesma empresa, então há soft-delete, uma idempotência e uma auditoria de sucesso | `RevokeMembershipServiceIntegrationTests.Revoking_an_active_same_company_membership_soft_deletes_it_and_audits_the_transition` | coberto |
| replay e estado terminal | Dado `request_id` repetido após soft-delete, quando o payload é igual, então retorna sucesso original sem auditoria duplicada; com payload diferente retorna conflito; com novo pedido no alvo revogado retorna não encontrado | `RevokeMembershipServiceIntegrationTests.Replaying_a_revocation_after_soft_delete_returns_the_original_result_without_duplicate_audit`; `Reusing_a_revocation_request_with_a_different_target_returns_idempotency_conflict`; `A_new_request_for_an_already_revoked_membership_is_hidden_as_not_found` | coberto |
| acesso e isolamento | Dado ator sem membership ativa, escopo diferente de `company`, capability removida antes do replay ou alvo Beta, quando tenta revogar Alfa, então nega antes de replay e não revela/altera outra empresa | `RevokeMembershipServiceIntegrationTests.A_revoked_actor_membership_is_denied_and_audited`; `Restricted_scope_does_not_authorize_a_company_membership_revocation`; `Authorization_is_evaluated_before_a_matching_replay`; `A_cross_company_target_membership_is_hidden_as_not_found_and_audited` | coberto |
| auditoria, telemetria e cancelamento | Dada falha do append, quando inicia revogação, então rollback preserva a membership e mede `membership_revocation`; cancelamento propaga sem mutação | `RevokeMembershipServiceIntegrationTests.Audit_failure_rolls_back_the_membership_revocation_and_reports_membership_telemetry`; `Cancellation_propagates_without_revoking_the_target` | coberto |
| corrida | Dadas 16 tentativas simultâneas com mesmo pedido, quando disputam a revogação, então há uma transição, uma auditoria e uma idempotência; todos recebem resultado equivalente | `RevokeMembershipServiceIntegrationTests.Sixteen_concurrent_retries_create_one_transition_audit_and_idempotency_record` | coberto |
| BDD Alfa/Beta/sessão | Dada a mesma sessão autenticada que já consultou memberships Alfa e Beta, quando o serviço interno revoga Alfa, então a nova consulta no mesmo cliente continua aceita e lista apenas Beta | cenário BDD `Membership revocation keeps the same session isolated to Beta` | coberto |

## Limites mantidos

Nenhuma regra de recurso para `self`, `team`, `unit` ou `group` foi criada.
Alfa/Beta descrevem isolamento de empresa nos testes; não representam dados de
produção. Auditoria e concorrência permanecem nos serviços que possuem a
transação.

O mapa não recebe empresa, identidade ou corpo. Ele é descoberta de leitura
para o ator já autenticado; não expõe papel, e-mail, issuer, subject, token ou
linhas de domínio.
