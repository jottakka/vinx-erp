# Casos públicos — issue 127, política e aprovação VINX (PR 2)

## Escopo desta fatia

Esta fatia entrega aprovações duráveis de uso único (`foundation.approvals`)
e o coordenador de confirmação single-flight do módulo AI. As cinco classes de
cenários BDD são materializadas como **testes xunit + este manifesto**; o
projeto Reqnroll não é expandido. Banco real (Postgres 17 em Testcontainers)
nos testes de integração da Foundation; framework real com modelo falso
determinístico nos testes do módulo AI.

## Classe 1 — sucesso/replay

| Cenário (Dado/Quando/Então) | Evidência xunit | Situação |
|---|---|---|
| Dada proposta preparada, quando a confirmação autorizada chega com o mesmo payload, então o consumo ocorre uma vez com relógio do banco e auditoria | `ApprovalServicesIntegrationTests.Confirming_a_live_proposal_claims_it_once_with_the_database_clock_and_audits` | coberto |
| Dada confirmação vencedora, quando o mesmo `RequestId` de confirmação é repetido, então o resultado original volta sem nova auditoria | `ApprovalServicesIntegrationTests.Replaying_the_winning_confirmation_returns_the_original_outcome_without_new_audit`; `HarnessConfirmationLifecycleTests.ReplayedConfirmation_WithSameRequestId_DoesNotResumeSessionTwice` | coberto |
| Dada preparação, quando o mesmo `RequestId` de preparação é repetido, então a mesma aprovação volta sem auditoria duplicada | `ApprovalServicesIntegrationTests.Replaying_the_preparation_request_returns_the_same_approval_without_duplicate_audit` | coberto |
| Dado payload com chaves em ordem diferente, quando prepare e confirm re-deriva o hash canônico, então o consumo ocorre | `ApprovalServicesIntegrationTests.Canonical_payload_key_ordering_matches_between_preparation_and_confirmation` | coberto |
| Dado `RequestId` de execução, quando a escrita confirmada é repetida, então o resultado original volta sem nova mutação | `HarnessConfirmationLifecycleTests.RequestAwareWrite_IsIdempotentByExecutionRequestId` | coberto |
| Dada proposta viva registrada, quando o vencedor confirma, então a sessão retoma uma vez e o estado em processo é removido | `HarnessConfirmationLifecycleTests.ConfirmedProposal_ExecutesOnce_AndRemovesInProcessState` | coberto |

## Classe 2 — validação/erros

| Cenário | Evidência xunit | Situação |
|---|---|---|
| Dados identificadores vazios/payload inválido/TTL inválido, quando o comando é construído, então falha estrutural antes de qualquer efeito | `ApprovalContractsTests.Prepare_command_validates_identifiers_payload_and_policy_version`; `ApprovalContractsTests.Confirm_and_veto_commands_validate_their_identifiers` | coberto |
| Dado hash em formato inválido, quando validado, então é rejeitado (SHA-256 hex minúsculo, 64 caracteres) | `ApprovalContractsTests.IsValidHash_enforces_sha256_lowercase_hexadecimal_format` | coberto |
| Dado id desconhecido, quando confirma, então `proposal_unavailable` sem mutação e sem negação auditada | `ApprovalServicesIntegrationTests.Confirming_an_unknown_approval_is_unavailable_without_mutation_or_denial_audit` | coberto |
| Dados payload ou policy version divergentes, quando confirma, então `proposal_conflict` sem mutação e com negações auditadas | `ApprovalServicesIntegrationTests.Confirmations_with_payload_or_policy_divergence_are_conflicts_and_audited`; `...Confirming_with_a_divergent_proposal_identifier_is_a_conflict` | coberto |
| Dado `RequestId` de preparação reutilizado com payload diferente, então `idempotency_conflict` | `ApprovalServicesIntegrationTests.Reusing_the_preparation_request_with_a_different_payload_is_an_idempotency_conflict` | coberto |
| Dada resposta de aprovação sem correspondência, quando validada pelo gate, então erro explícito (não é no-op silencioso) | `HarnessConfirmationLifecycleTests.UnmatchedApprovalResponse_NoOpBecomesExplicitError` | coberto |

## Classe 3 — acesso

| Cenário | Evidência xunit | Situação |
|---|---|---|
| Não autenticado | — | **adiado/N/A nesta fatia**: não existe fronteira de host/autenticação no módulo AI (sem endpoints HTTP); a barreira de autenticação pertence ao host (issue #138). A ausência de contexto é testada como fail-closed (linhas abaixo). |
| Dado ator autenticado sem membership, quando prepara, então negação auditada e nada persistido | `ApprovalServicesIntegrationTests.Preparation_without_membership_is_denied_and_audited` | coberto |
| Dado ator sem a capability `ai.writes.confirm` (ou com escopo menor que `company`), quando prepara, então `capability_denied` auditado | `ApprovalServicesIntegrationTests.Preparation_without_the_capability_is_denied_and_audited`; `...A_restricted_capability_scope_does_not_authorize_preparation` | coberto |
| Dada proposta de outro ator, quando confirmada por outro usuário, então invisível (`proposal_unavailable`) via RLS | `ApprovalServicesIntegrationTests.Cross_actor_...` (matriz RLS) | coberto |
| Dada proposta de outra empresa, quando confirmada, então invisível via RLS sem revelar existência | `ApprovalServicesIntegrationTests.Cross_company_...` (matriz RLS) | coberto |
| Dado contexto confiável ausente ou incompleto (`Guid.Empty`), quando avalia, então nega fail-closed | `HarnessApprovalBoundaryTests.MissingTrustedContext_FailsClosed`; `ApprovalServicesIntegrationTests` (ausência de contexto RLS) | coberto |
| Dada segunda preparação com proposta viva, então negação auditada fail-closed (nunca substituição silenciosa) | `ApprovalServicesIntegrationTests.A_second_preparation_while_a_proposal_is_live_is_denied_fail_closed_and_audited` | coberto |
| Privilégios mínimos por coluna do runtime | `ApprovalsMigrationTests` (matriz `has_column_privilege`) + `FoundationSchemaTests.ExpectedRuntimeTableGrants` (`SELECT`/`INSERT`) | coberto |

## Classe 4 — estado/exceções

| Cenário | Evidência xunit | Situação |
|---|---|---|
| Dada proposta expirada, quando confirma, então `proposal_unavailable` sem mutação | `ApprovalServicesIntegrationTests.An_expired_proposal_cannot_be_confirmed`; `HarnessConfirmationLifecycleTests.ExpiredProposal_IsUnavailable_AndNeverExecutes` | coberto |
| Dado veto, quando aplicado, então soft-delete com motivo fixo, auditoria e consumo impossível; repetição é idempotente | `ApprovalServicesIntegrationTests` (ciclo de veto + replay de veto) | coberto |
| Dado veto após consumo, então conflito (consumo é terminal) | `ApprovalServicesIntegrationTests` (veto após consumo) | coberto |
| Dada proposta consumida, quando novo `RequestId` confirma, então conflito auditado | `ApprovalServicesIntegrationTests.A_confirmation_with_a_different_request_after_consumption_is_a_conflict_and_audited` | coberto |
| Dado veto no coordenador, quando aplicado, então o veto volta à sessão e o estado é removido | `HarnessConfirmationLifecycleTests.Veto_RemovesState_AndDoesNotExecute` | coberto |
| Dado reinício do processo, quando a confirmação chega, então `ProposalUnavailable` sem promessa de replay de resposta | `HarnessConfirmationLifecycleTests.ProcessRestart_InvalidatesLiveProposal_WithoutPromisingResponseReplay` | coberto |
| Dada falha de auditoria, então rollback total (padrão `RevokeMembershipService` herdado) | comportamento transacional coberto pelos testes de serviço | coberto |

## Classe 5 — concorrência

| Cenário | Evidência xunit | Situação |
|---|---|---|
| Dados 8 confirms simultâneos com o mesmo `RequestId`, então 1 commit + 7 replays + 1 auditoria | `ApprovalServicesIntegrationTests.Concurrent_confirmations_with_the_same_request_produce_one_claim_and_replays` | coberto |
| Dados 4 confirms simultâneos com `RequestId` distintos, então 1 sucesso + 3 negações auditadas | `ApprovalServicesIntegrationTests.Concurrent_confirmations_with_distinct_requests_produce_one_claim_and_audited_conflicts` | coberto |
| Dadas confirmações concorrentes no coordenador, então single-flight: uma execução e reuso do resultado do vencedor | `HarnessConfirmationLifecycleTests.ConcurrentConfirmationsWithSameRequestId_ExecuteExactlyOnce` | coberto |

## Provas da PR 1 mantidas

Todas as provas de `HarnessApprovalBoundaryTests` permanecem verdes após a
migração de ator/empresa para `Guid`: leitura framework-mediated; escrita nunca
executa sem confirmação; lotes fail-closed; mesma sessão/solicitação exata;
nenhuma autoaprovação; nenhum loop manual.

## Limites mantidos

Nenhum host muda; nenhum dado de domínio é tocado pelas sondas. Segredos,
tokens, prompts e payloads não aparecem em código, teste ou log. Retenção e
limpeza de aprovações expiradas: issue #201.
