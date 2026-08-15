# Manifesto público — issue 127, política e aprovação durável

## Escopo e comandos

A Foundation mantém preparação, confirmação e veto duráveis com PostgreSQL
real. O módulo AI mantém somente o gate de classificação; não existe
coordenador, endpoint ou entrega de aprovação ao agente.

| Código | Comando exato |
|---|---|
| AI | `dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q` |
| FU | `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests --no-restore --nologo -v q` |
| AS | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter FullyQualifiedName~ApprovalServicesIntegrationTests --blame-hang-timeout 60s --blame-hang-dump-type none -v q` |
| AM | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter FullyQualifiedName~ApprovalsMigrationTests --blame-hang-timeout 60s --blame-hang-dump-type none -v q` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q` |

## Cinco classes BDD

| Classe | Garantia | Evidência pública |
|---|---|---|
| sucesso | Replay da preparação devolve a mesma aprovação sem auditoria duplicada | `ApprovalServicesIntegrationTests.Replaying_the_preparation_request_returns_the_same_approval_without_duplicate_audit` |
| sucesso | Replay do pedido vencedor devolve o resultado original | `ApprovalServicesIntegrationTests.Replaying_the_winning_confirmation_returns_the_original_outcome_without_new_audit` |
| validação/erros | Identificadores, payload, TTL, policy e hash inválidos falham | `ApprovalContractsTests.Prepare_command_validates_identifiers_payload_and_policy_version`; `ApprovalContractsTests.IsValidHash_enforces_sha256_lowercase_hexadecimal_format` |
| validação/erros | Payload ou policy divergente não muta a proposta e audita a negação | `ApprovalServicesIntegrationTests.Confirmations_with_payload_or_policy_divergence_are_conflicts_and_audited` |
| acesso | Membership ausente ou capability restrita não autoriza | `ApprovalServicesIntegrationTests.Preparation_without_membership_is_denied_and_audited`; `ApprovalServicesIntegrationTests.A_restricted_capability_scope_does_not_authorize_preparation` |
| acesso | Empresa ou ator cruzado permanecem ocultos por RLS | `ApprovalServicesIntegrationTests.A_cross_company_confirmation_is_hidden_as_unavailable`; `ApprovalServicesIntegrationTests.A_cross_actor_confirmation_within_the_same_company_is_hidden_as_unavailable` |
| estado/exceções | Expiração, veto idempotente e rollback de auditoria preservam o estado válido | `ApprovalServicesIntegrationTests.An_expired_proposal_cannot_be_confirmed`; `ApprovalServicesIntegrationTests.Replaying_a_veto_is_idempotent_and_vetoing_a_consumed_approval_is_a_conflict`; `ApprovalServicesIntegrationTests.Confirmation_audit_failure_rolls_back_the_claim_and_leaves_the_approval_live` |
| concorrência | Preparações e confirmações concorrentes produzem um vencedor e replays/conflitos coerentes | `ApprovalServicesIntegrationTests.Concurrent_preparation_replays_when_the_first_idempotency_read_precedes_the_winner_commit`; `ApprovalServicesIntegrationTests.Concurrent_confirmations_with_the_same_request_produce_one_claim_and_replays`; `ApprovalServicesIntegrationTests.Concurrent_confirmations_with_distinct_requests_produce_one_claim_and_audited_conflicts` |
| fronteira AI | Escrita fica pendente e não há superfície pública de entrega | `HarnessApprovalBoundaryTests.Write_without_server_delivery_remains_pending_and_unexecuted`; `HarnessApprovalBoundaryTests.Public_surface_is_text_only_and_has_no_approval_delivery` |

## Limite explícito

Esses testes não provam um fluxo ponta a ponta de aprovação do agente. A
Foundation oferece a primitiva durável, mas nenhum host a conecta ao Agent
Framework. Autenticação de host, endpoint e estratégia de retomada pertencem à
issue #138 e devem ser desenhados após a baseline de backend.
