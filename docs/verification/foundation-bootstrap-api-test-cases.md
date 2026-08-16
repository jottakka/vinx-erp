# Casos BDD — bootstrap autenticado Foundation

## Escopo

`GET /foundation/bootstrap` atende somente à pessoa já admitida pelo gate. Não
recebe corpo, usuário, empresa, capability ou flag do cliente. A resposta traz
`user` self-only, memberships ativas ordenadas com capabilities efetivas e
`flags` como mapa `string -> bool`.

Não expõe papel, e-mail, issuer, subject, token, sessão ou empresa não
autorizada. Uma pessoa autenticada sem membership recebe `companies: []`.

| Classe BDD | Cenário | Evidência executável | Resultado esperado |
| --- | --- | --- | --- |
| Sucesso | Dada pessoa admitida com memberships, quando consulta bootstrap, então recebe perfil próprio, empresas ordenadas, capabilities com escopo e flags vazias; com configuração válida, recebe somente o mapa booleano materializado no início do host. | `BootstrapEndpointTests.Bootstrap_returns_the_current_profile_and_only_its_ordered_memberships`; `BootstrapEndpointTests.Bootstrap_allows_an_authenticated_person_without_memberships`; `BootstrapEndpointTests.Bootstrap_projects_configured_flags_and_keeps_the_host_snapshot` | `200`; sem membership também é `200`; alteração de configuração no processo não altera a resposta. |
| Validação/erros | Dada flag inválida, duplicada sem distinção de casing ou incompleta, quando o host inicia, então falha. Dado perfil ausente ou leitura indisponível, não há resposta parcial. Dado cancelamento, ele propaga. | `ApiHostPlatformTests.Invalid_bootstrap_flag_configuration_fails_host_startup`; `Bootstrap_flag_configuration_with_missing_metadata_fails_host_startup`; `Bootstrap_flag_configuration_with_missing_enabled_fails_host_startup`; `Bootstrap_flag_configuration_with_each_required_metadata_field_missing_fails_host_startup`; `Bootstrap_feature_flags_reject_casing_only_duplicate_keys`; `BootstrapEndpointTests.Bootstrap_service_returns_only_audit_unavailable_when_profile_is_absent`; `Bootstrap_service_propagates_cancellation` | Startup falha para configuração inválida; leitura indisponível retorna somente `503 {"code":"audit_unavailable"}`; cancelamento não vira `503`. |
| Acesso | Dada chamada anônima ou identidade externa desconhecida, quando consulta bootstrap, então o gate responde antes da leitura. Dada resposta válida, então não contém dados sensíveis nem empresa alheia. O contrato OpenAPI Development declara bearer na operação. | `BootstrapEndpointTests.Bootstrap_rejects_an_anonymous_request`; `Bootstrap_rejects_an_unknown_external_identity_before_reading_a_profile`; `Bootstrap_returns_the_current_profile_and_only_its_ordered_memberships`; `BootstrapProfileMigrationTests`; `ApiHostPlatformTests.Bootstrap_openapi_declares_bearer_authentication_and_all_public_outcomes` | `401` vazio pelo gate; resposta self-only; grants mínimos verificados em PostgreSQL; bearer HTTP na especificação. |
| Estado/exceções | Dada revogação concluída, quando ocorre a próxima leitura, então a membership revogada não aparece. Falha do gate ou da leitura retorna código estável; `429` é resultado declarado. Falha de perfil registra somente log estruturado seguro. | `BootstrapEndpointTests.Bootstrap_excludes_a_revoked_membership_on_the_next_request`; `IdentityGateEndpointTests.Resolution_database_failure_returns_only_the_safe_identity_gate_code`; `IdentityGateEndpointTests.Admission_database_failure_returns_only_the_safe_identity_gate_code`; `Bootstrap_returns_a_safe_service_error_when_profile_function_execution_is_denied`; `Bootstrap_returns_a_safe_service_error_when_membership_function_execution_is_denied`; `Bootstrap_service_logs_one_safe_warning_when_profile_is_absent`; `ApiHostPlatformTests.Bootstrap_openapi_declares_bearer_authentication_and_all_public_outcomes` | Próxima leitura reflete revogação; `503` contém somente `identity_gate_unavailable` ou `audit_unavailable`; OpenAPI declara `429`; log não contém identificadores do ator ou correlação. |
| Concorrência | Dada leitura do resolver em curso e revogação concorrente, então o snapshot materializado não muda; a próxima leitura observa a revogação. O endpoint reutiliza esse resolver e não introduz barreira própria. | `MembershipMapEndpointTests.In_flight_read_keeps_the_materialized_membership_visible_after_concurrent_revoke`; `MembershipMapEndpointTests.Concurrent_revoke_does_not_change_an_in_flight_set_but_changes_the_next_read`; `BootstrapEndpointTests.Bootstrap_excludes_a_revoked_membership_on_the_next_request` | Snapshot em curso preservado; somente a próxima leitura muda. |

## Limites mantidos

Não há CRUD ou UI de flags, criação de empresa, operador global, role ou
capability administrativa nesta rota. `429` é documentado e herdado do limite
de taxa do grupo; este documento não afirma uma prova de saturação exclusiva da
rota.
