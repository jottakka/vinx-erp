# Casos públicos — issue 94, inbox in-app, contador e marcação de leitura

## Escopo desta fatia

Consulta paginada da inbox in-app autorizada, contador de não lidas e
marcação idempotente de leitura (#94). Read state como colunas
`read_at`/`read_by_user_id` em `notifications.notification_intents`; visibilidade
= `in_app` + `delivered` + destinatário = ator + membership ativa
(`foundation.current_actor_memberships()`); três funções SECURITY DEFINER com
EXECUTE somente para `vinx_runtime`; envelope HTTP allowlistado com allowlist
fechada por chave completa; outbox/worker/provider/WhatsApp/Paguai intocados.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado ator com intents in_app entregues em duas empresas, quando consulta a inbox, então somente itens autorizados aparecem com a empresa de origem | `NotificationInboxTests.Authorized_actor_lists_delivered_in_app_intents_with_company_of_origin` | coberto |
| sucesso | Dado janela keyset, quando pagina com cursor, então nenhuma página duplica ou perde item e `nextCursor` fecha no fim | `NotificationInboxTests.Pagination_is_stable_without_duplicates_or_losses` | coberto |
| sucesso | Dado insert concorrente à frente do cursor, quando continua paginando, então o item novo aparece somente após refresh, sem duplicação | `NotificationInboxTests.Insert_ahead_of_the_cursor_is_visible_only_after_refresh` | coberto |
| sucesso | Dado itens não lidos, quando consulta contador e marca leitura, então o contador acompanha exatamente | `NotificationInboxTests.Unread_count_follows_the_listed_items_and_marking` | coberto |
| sucesso | Dado seletor allowlistado (`/paguai`), quando projeta o item, então `action` segura; seletor/chave desconhecidos rendem `action`/`event` nulos sem ecoar valores brutos | `NotificationInboxTests.Allowlisted_deep_links_become_safe_actions_and_unknown_keys_stay_generic` | coberto |
| validação | Dado cursor inválido (vazio, não-canônico, versão errada, não-numérico, >128 bytes), quando lista via serviço, então `Invalid` sem efeitos | `NotificationInboxTests.Invalid_cursors_are_rejected_without_side_effects` | coberto |
| validação | Dado limite fora de 1..50, quando lista, então `Invalid` | `NotificationInboxTests.OutOf_range_limits_are_rejected` | coberto |
| validação | Dado comando de leitura repetido, quando marca duas vezes, então mesmo resultado com uma única transição auditada | `NotificationInboxTests.Repeated_read_is_idempotent_and_audits_once` | coberto |
| validação | Dado HTTP, quando query/id inválidos, então `400 invalid_request`; guid malformado cai em `404` de rota; `Guid.Empty` é `400` | `NotificationInboxTests.Inbox_http_validation_maps_bad_queries_and_ids_to_safe_errors` | coberto |
| validação | Dado envelope HTTP, quando inspeciona item, então somente as 7 chaves allowlistadas, sem `event_key`/seletor/template no payload | `NotificationInboxTests.Inbox_item_payload_exposes_only_allowlisted_fields` | coberto |
| validação | Dado cursor gerado, quando decodifica/re-encoda, então round-trip exato com wire base64url sem padding | `NotificationInboxCursorTests.Round_trip_preserves_the_keyset_position` + `Tokens_carry_only_the_version_micros_and_uuid_payload` | coberto |
| acesso | Dado membro ativo sem qualquer capability, quando consulta, então a inbox pessoal aparece | `NotificationInboxAccessTests.Active_member_without_any_capability_still_sees_the_personal_inbox` | coberto |
| acesso | Dado destinatário estranho ao ator, quando consulta lista/contador, então vazio e zero, sem vazamento | `NotificationInboxAccessTests.Non_member_recipient_is_invisible_and_uncounted_for_the_current_actor` | coberto |
| acesso | Dado entrega fora do escopo vs. inexistente, quando marca, então `404` idêntico (indistinguível) | `NotificationInboxAccessTests.Out_of_scope_delivery_returns_the_same_404_as_a_missing_one` | coberto |
| acesso | Dado membership revogada, quando reconsulta, então itens daquela empresa somem da lista e do contador (BDD #94) | `NotificationInboxAccessTests.Revoked_membership_removes_entries_from_list_and_count` | coberto |
| acesso | Dado empresa excluída, quando reconsulta, então itens somem | `NotificationInboxAccessTests.Soft_deleted_company_removes_entries_from_list` | coberto |
| acesso | Dado catálogo de privilégios, quando verificado, então runtime sem DML direto na tabela, funções executáveis só por `vinx_runtime`, worker sem acesso, owner de notificações com EXECUTE em `current_actor_memberships` | `NotificationInboxAccessTests.Runtime_keeps_no_direct_table_access_and_functions_run_only_for_runtime` | coberto |
| estado | Dado intent whatsapp entregue, quando consulta a inbox in-app, então nunca aparece | `NotificationInboxStateTests.Delivered_whatsapp_intent_never_appears_in_the_in_app_inbox` | coberto |
| estado | Dado cada estado não publicável (`pending_delivery`, `pending_provider`, `provider_in_flight`, `provider_failed`, `provider_ambiguous`), quando lista/marca, então não lista e não marca (`404`) | `NotificationInboxStateTests.Non_delivered_intents_are_not_listed_and_not_markable` | coberto |
| estado | Dado falha de auditoria (REVOKE de `append_event`), quando marca, então transação inteira falha fechada sem `read_at` e sem auditoria parcial | `NotificationInboxStateTests.Audit_failure_rolls_back_the_marking_without_partial_state` | coberto |
| estado | Dado leitura/contagem/marcação, quando compara colunas do worker, então `status`, `attempt_count` e `provider_*` intocados | `NotificationInboxStateTests.Inbox_operations_do_not_touch_worker_lifecycle_columns` | coberto |
| estado | Dado sessão cookie BFF, quando POST sem CSRF, então `400`; com token fresco e via bearer, então `200` com uma auditoria | `NotificationInboxStateTests.Cookie_session_requires_csrf_for_read_and_accepts_a_fresh_token` | coberto |
| estado | Dado requisições anônimas até o limite, quando extrapolam, então `401`/`429` com corpo vazio e `private, no-store` em todas | `NotificationInboxTests.Unauthenticated_and_rate_limited_responses_are_empty_bodies_with_no_store` | coberto |
| concorrência | Dados duas conexões `vinx_runtime` chamando a função real, quando A retém o lock, então B espera (`pg_stat_activity` `pid = B AND wait_event_type = 'Lock'`) e termina `already_read`; final com 1 `read_at`, 1 auditoria, contador −1 | `NotificationInboxConcurrencyTests.Concurrent_marks_via_the_real_function_produce_a_single_transition_with_lock_proof` | coberto |
| concorrência | Dados dois scopes de serviço disputando a mesma entrega, quando ambos terminam, então uma única transição auditada | `NotificationInboxConcurrencyTests.Service_level_concurrent_marks_end_with_a_single_audited_transition` | coberto |
| concorrência | Dado contador correndo contra marcação, quando finaliza, então valor final exato (0) e intermediário válido | `NotificationInboxConcurrencyTests.Count_racing_a_mark_observes_valid_values_and_the_final_count_is_exact` | coberto |

## Limites mantidos

Nenhuma alteração em outbox, worker, provider, DLQ, métricas, WhatsApp ou
Paguai; estados pendentes nunca listam nem são marcáveis; sem PII, corpo,
template ou payload em resposta, log ou auditoria; cursor opaco sem valor de
autoridade; escopo recalculado por membership a cada chamada, sem aceitar
`companyId`/destinatário do cliente. A UI consumidora é #95 e depende deste
contrato congelado.
