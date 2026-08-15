# Manifesto público — issue 127, política e aprovação VINX (PR 2)

## Escopo e comandos

Os cenários BDD são materializados pelos cinco conjuntos xUnit abaixo. As
integrações usam PostgreSQL real; AI usa o framework real com modelo
determinístico. `quality/private` não faz parte desta evidência.

| Código | Comando exato | Resultado |
|---|---|---|
| AI | `dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q` | 44/44, coberto |
| FU | `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests --no-restore --nologo -v q` | 118/118, coberto |
| WI | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter FullyQualifiedName~ApprovalCoordinatorDurableLifecycleTests --blame-hang-timeout 60s --blame-hang-dump-type none -v q` | 6/6, coberto |
| AS | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter FullyQualifiedName~ApprovalServicesIntegrationTests --blame-hang-timeout 60s --blame-hang-dump-type none -v q` | 31/31, coberto |
| AM | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter FullyQualifiedName~ApprovalsMigrationTests --blame-hang-timeout 60s --blame-hang-dump-type none -v q` | 6/6, coberto |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q` | 17/17, coberto |

## Cinco classes BDD

### Classe 1 — sucesso, replay e idempotência

| Dado / quando / então | Teste xUnit atual | Status / comando |
|---|---|---|
| Dada preparação, replay do mesmo `RequestId` devolve a mesma aprovação sem auditoria duplicada | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Replaying_the_preparation_request_returns_the_same_approval_without_duplicate_audit` | Coberto / AS |
| Dado claim vencedor, replay do mesmo pedido devolve o resultado original sem nova auditoria | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Replaying_the_winning_confirmation_returns_the_original_outcome_without_new_audit` | Coberto / AS |
| Dado timeout pós-commit, só o mesmo pedido adota a propriedade e executa uma vez | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Post_commit_timeout_recovers_only_same_request_with_replay_and_one_stable_execution` | Coberto / WI |
| Dado payload com outra ordem de chaves, prepare e confirm usam o mesmo hash canônico | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Canonical_payload_key_ordering_matches_between_preparation_and_confirmation` | Coberto / AS |

### Classe 2 — validação e erros fail-closed

| Dado / quando / então | Teste xUnit atual | Status / comando |
|---|---|---|
| Identificadores, payload, TTL ou policy inválidos falham na construção | `tests/public/Vinx.Erp.Foundation.UnitTests/ApprovalContractsTests.cs::ApprovalContractsTests.Prepare_command_validates_identifiers_payload_and_policy_version` | Coberto / FU |
| Hash que não é SHA-256 hexadecimal minúsculo de 64 caracteres é rejeitado | `tests/public/Vinx.Erp.Foundation.UnitTests/ApprovalContractsTests.cs::ApprovalContractsTests.IsValidHash_enforces_sha256_lowercase_hexadecimal_format` | Coberto / FU |
| Divergência de payload/policy/proposal não muta a proposta e audita a negação | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Confirmations_with_payload_or_policy_divergence_are_conflicts_and_audited` | Coberto / AS |
| Permit/decisão de aprovação inválida falha antes da execução | `tests/public/Vinx.Erp.AI.UnitTests/HarnessApprovalBoundaryTests.cs::HarnessApprovalBoundaryTests.Delivery_permit_negative_matrix_fails_closed_before_agent_execution` | Coberto / AI |

### Classe 3 — acesso, empresa e contexto confiável

| Dado / quando / então | Teste xUnit atual | Status / comando |
|---|---|---|
| Sem membership, preparação é negada e auditada | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Preparation_without_membership_is_denied_and_audited` | Coberto / AS |
| Capability ausente ou escopo restrito não autoriza | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.A_restricted_capability_scope_does_not_authorize_preparation` | Coberto / AS |
| Empresa/ator cruzado ficam indisponíveis via RLS | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.A_cross_company_confirmation_is_hidden_as_unavailable` e `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.A_cross_actor_confirmation_within_the_same_company_is_hidden_as_unavailable` | Coberto / AS |
| Sem contexto de sessão, consulta runtime falha fechada | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.A_runtime_query_without_session_context_is_fail_closed` | Coberto / AS |
| Host não autenticado | N/A: não existe fronteira HTTP nesta fatia; vinculada à issue #138 | N/A, não é blocker |

### Classe 4 — estado terminal e exceções

| Dado / quando / então | Teste xUnit atual | Status / comando |
|---|---|---|
| Proposta expirada não confirma | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.An_expired_proposal_cannot_be_confirmed` | Coberto / AS |
| Veto grava motivo terminal, audita, não escreve e entrega uma vez | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Durable_veto_is_delivered_once_and_persists_terminal_audit_without_write` | Coberto / WI |
| Replay de veto é idempotente; veto após consumo é conflito | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Replaying_a_veto_is_idempotent_and_vetoing_a_consumed_approval_is_a_conflict` | Coberto / AS |
| Falha de auditoria desfaz claim e mantém proposta viva | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Confirmation_audit_failure_rolls_back_the_claim_and_leaves_the_approval_live` | Coberto / AS |
| Shutdown impede confirmação/veto tardios e entrega ao agente | `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Shutdown_started_before_timer_quiescence_rejects_late_confirm_without_delivery`, `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Shutdown_started_before_timer_quiescence_rejects_late_veto_without_delivery`, `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Shutdown_winning_confirmation_handoff_never_starts_agent_delivery` e `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Shutdown_winning_veto_handoff_never_starts_agent_delivery` | Coberto / AI |

### Classe 5 — concorrência e single-flight

| Dado / quando / então | Teste xUnit atual | Status / comando |
|---|---|---|
| Confirmações concorrentes com o mesmo pedido produzem um claim e replays | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Concurrent_confirmations_with_the_same_request_produce_one_claim_and_replays` | Coberto / AS |
| Pedidos distintos concorrentes produzem um claim e conflitos auditados | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalServicesIntegrationTests.cs::ApprovalServicesIntegrationTests.Concurrent_confirmations_with_distinct_requests_produce_one_claim_and_audited_conflicts` | Coberto / AS |
| Coordenador alcança Foundation a cada confirmação e executa uma vez | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Concurrent_same_confirmation_request_reaches_foundation_each_time_and_executes_once` | Coberto / WI |
| Outro pedido após claim não adota nem executa | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Different_request_after_committed_unobserved_claim_is_conflict_and_cannot_execute` | Coberto / WI |

## Oito blockers da fatia

| Blocker | Garantia mínima | Evidência xUnit atual | Status / comando |
|---|---|---|---|
| B1 | Feixe público opaco com somente dois métodos | `tests/public/Vinx.Erp.AI.UnitTests/HarnessApprovalBoundaryTests.cs::HarnessApprovalBoundaryTests.Public_bundle_is_text_only_and_exposes_no_raw_agent_surface` | Coberto / AI |
| B2 | Permit vinculado à proposta/`CallId` e consumido uma vez | `tests/public/Vinx.Erp.AI.UnitTests/HarnessApprovalBoundaryTests.cs::HarnessApprovalBoundaryTests.Public_approval_delivery_methods_require_proposal_and_unforgeable_permit` e `tests/public/Vinx.Erp.AI.UnitTests/HarnessApprovalBoundaryTests.cs::HarnessApprovalBoundaryTests.Permit_consumes_once_and_records_only_bound_execution_request_id` | Coberto / AI |
| B3 | Loop real do framework; escrita sem entrega do servidor não executa | `tests/public/Vinx.Erp.AI.UnitTests/HarnessApprovalBoundaryTests.cs::HarnessApprovalBoundaryTests.Read_only_user_turn_executes_through_real_harness` e `tests/public/Vinx.Erp.AI.UnitTests/HarnessApprovalBoundaryTests.cs::HarnessApprovalBoundaryTests.Write_without_server_delivery_remains_pending_and_unexecuted` | Coberto / AI |
| B4 | Autorização Foundation é reavaliada no replay e RLS oculta revogação | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Membership_revocation_before_same_request_replay_is_reauthorized_without_cached_execution` e `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Capability_revocation_before_same_request_replay_is_reauthorized_without_cached_execution` | Coberto / WI |
| B5 | Timeout pós-commit recupera somente o mesmo `RequestId` | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Post_commit_timeout_recovers_only_same_request_with_replay_and_one_stable_execution` | Coberto / WI |
| B6 | Veto durável, `VetoStart` e auditoria sem escrita | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalCoordinatorDurableLifecycleTests.cs::ApprovalCoordinatorDurableLifecycleTests.Durable_veto_is_delivered_once_and_persists_terminal_audit_without_write` | Coberto / WI |
| B7 | Constraints, índices parciais e classifier têm nomes/semântica fixos | `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalsMigrationTests.cs::ApprovalsMigrationTests.Approval_terminal_constraints_reject_partial_consumption_and_consumed_deleted_rows`, `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApprovalsMigrationTests.cs::ApprovalsMigrationTests.Approval_unique_indexes_reject_live_actor_and_confirmation_request_collisions` e `tests/public/Vinx.Erp.Foundation.UnitTests/ApprovalContractsTests.cs::ApprovalContractsTests.Unique_violation_classifier_routes_only_pinned_constraint_names` | Coberto / AM + FU |
| B8 | Terminal retention/sweep, shutdown gate e cleanup ordenado | `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Sweep_publishes_cleanup_before_removal_so_dispose_observes_gated_resource_cleanup`, `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Dispose_quiesces_timer_cancels_work_completes_tcs_and_defers_shutdown_cleanup`, `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Shutdown_winning_confirmation_handoff_never_starts_agent_delivery` e `tests/public/Vinx.Erp.AI.UnitTests/HarnessConfirmationLifecycleTests.cs::HarnessConfirmationLifecycleTests.Shutdown_winning_veto_handoff_never_starts_agent_delivery` | Coberto / AI |

## Hardening deferido pelo dono

Não são blockers: trigger/advisory matrix em ordem forçada; contagens exatas
de descarte por recurso ou instrumentação privada; reflexão ampla; agente
sintético grande; interleavings exaustivos; stress/soak e repetição grande.
Probes adicionais sem poder de detectar bypass de autorização, acesso cruzado,
escrita duplicada, claim/veto/recovery incorreto, corrupção persistente,
entrega pós-shutdown ou falha do gate público também ficam deferidos. O ajuste
pequeno de `VetoStart` é coberto por B6.

## Limites

Nenhum host muda. Segredos, tokens, prompts e payloads não aparecem em código,
teste ou log. Retenção terminal em memória é TTL/sweep; purga física de linhas
expiradas permanece na issue #201.
