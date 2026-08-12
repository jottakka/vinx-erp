# Casos públicos — issue 19, autorização por empresa

## Escopo desta fatia

Esta fatia introduz a avaliação de capability por membership e o catálogo
durável de Foundation. Mapa autenticado de memberships e revogação continuam
em fatias posteriores; não há rota CRUD, grant novo ou alteração de RLS.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| catálogo | Dado banco novo, quando aplica migrations, então cada capability canônica existe uma vez com UUID estável; reaplicar não duplica e runtime somente lê | `CapabilityCatalogMigrationTests.Migration_seeds_each_known_capability_once_without_runtime_mutation_privileges` | coberto |
| catálogo legado | Dado nome canônico pré-existente com UUID legado, quando aplica migration, então preserva sua linha; UUID canônico ocupado por nome diferente falha | `CapabilityCatalogMigrationTests.Migration_preserves_the_existing_id_for_a_known_name_when_downgraded`; `CapabilityCatalogMigrationTests.Migration_fails_explicitly_when_a_known_id_belongs_to_a_different_name` | coberto |
| escopo | Dado grant `self`, `team`, `unit` ou `group`, quando executa comando de empresa, então nega; somente `company` satisfaz | `CapabilityEvaluatorIntegrationTests.Company_command_requires_an_exact_company_grant` | coberto |
| Alfa/Beta | Dado papel, grant ou assignment de outra empresa, quando avalia Alfa, então Beta não autoriza | `CapabilityEvaluatorIntegrationTests.Another_company_assignment_and_grant_never_authorize_the_requested_company` | coberto |
| auditoria | Dado ator sem grant efetivo, quando tenta atribuir papel ou enviar/confirmar documento, então nega com auditoria e sem mutação | `RoleAssignmentServiceIntegrationTests.Restricted_scope_does_not_authorize_a_company_role_assignment`; `DocumentUploadEndpointTests.Restricted_scopes_do_not_authorize_company_upload_commands` | coberto |
| concorrência | Dadas escritas concorrentes de atribuição, quando disputa a mesma operação, então idempotência e integridade preservam resultado seguro | `RoleAssignmentServiceIntegrationTests.Concurrent_assignment_retries_return_the_same_stored_result_without_audit_or_assignment_duplicates` | coberto |
| downgrade | Dada capability seeded referenciada, quando faz downgrade, então falha fechada sem remover catálogo | `CapabilityCatalogMigrationTests.Downgrade_fails_closed_when_a_seeded_capability_is_referenced` | coberto |

## Limites mantidos

Nenhuma regra de recurso para `self`, `team`, `unit` ou `group` foi criada.
Alfa/Beta descrevem isolamento de empresa nos testes; não representam dados de
produção. Auditoria e concorrência permanecem nos serviços que possuem a
transação.
