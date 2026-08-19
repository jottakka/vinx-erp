# Casos públicos — issue 48, worker de entregas de notificação (fatia W2)

## Escopo desta fatia

Esta fatia consome a fila durável `notification-delivery` criada em #26:
máquina de estados `pending_delivery → pending_provider → provider_in_flight
→ delivered|provider_failed|provider_ambiguous`, claim único por token de
tentativa com `FOR UPDATE`, liberação de lease para transitivos, retry finito
(1s/5s/30s) com DLQ redigida, métricas de baixa cardinalidade e a fronteira
`INotificationProvider` no módulo `Vinx.Erp.Notifications` (sem regra Meta;
provider in-app confirmado pela própria linha de intenção). O fluxo do convite
de dono não é alterado; webhooks e adapter Meta ficam para #28.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado destinatário in-app elegível enfileirado, quando o worker consome, então entrega concluída uma vez com auditoria de ingresso e provider e métrica `delivered` por canal | `NotificationWorkerTests.In_app_delivery_concludes_once_with_audit_and_outcome_metric` | coberto |
| sucesso | Dado destinatário WhatsApp com opt-in e provider que confirma, quando o worker consome, então `delivered` com exatamente uma chamada e uma tentativa | `NotificationWorkerTests.WhatsApp_delivery_with_delivered_provider_concludes_once` | coberto |
| validação | Dado envelope com delivery vazio ou correlação ausente, quando o handler executa, então dead letter imediato sem chamada externa | `NotificationWorkerSecurityTests.Invalid_envelope_is_dead_lettered_without_provider_call` | coberto |
| validação | Dado canal sem provider registrado, quando o worker consome, então dead letter `provider_missing` com corpo redigido (`deliveryId` zerado) e a entrega permanece `pending_provider` sem claim | `NotificationWorkerSecurityTests.Missing_provider_channel_is_dead_lettered_redacted_without_claiming_the_delivery` | coberto |
| estado | Dado timeout do provider, quando ele retorna ambíguo, então `provider_ambiguous` terminal com `failure_reason` estável e exatamente uma chamada — sem retry cego | `NotificationWorkerTests.WhatsApp_timeout_maps_to_terminal_ambiguous_without_retry` | coberto |
| estado | Dado rejeição definitiva do provider, quando ele retorna rejeitado, então `provider_failed` com `provider_rejected` e sem retry | `NotificationWorkerTests.WhatsApp_rejection_maps_to_terminal_provider_failed` | coberto |
| estado | Dado tentativa transitiva (infra/5xx), quando o provider lança transitivo, então o lease é liberado (`pending_provider`), o retry finito executa 4 tentativas e o esgotamento vai para a DLQ sem confirmação falsa | `NotificationWorkerTests.Transient_provider_errors_release_the_lease_and_exhaust_retries_without_false_confirmation` | coberto |
| estado | Dado entrega `provider_in_flight` por outra tentativa, quando o worker consome, então skip sem chamada externa e o status preservado | `NotificationWorkerTests.Delivery_in_flight_by_another_attempt_is_skipped_without_provider_call` | coberto |
| acesso | Dado worker sem `EXECUTE` nas funções, quando ele consome, então falha fechado: a entrega permanece `pending_delivery`, o host segue saudável e nada é confirmado | `NotificationWorkerSecurityTests.Worker_without_function_grants_fails_closed_without_losing_the_delivery` | coberto |
| acesso | Dado catálogo de privilégios, quando verificado, então as quatro funções são executáveis somente por `vinx_worker`; runtime e worker seguem sem DML direto nas tabelas | `NotificationWorkerSecurityTests.Worker_functions_are_executable_only_by_the_worker_role` | coberto |
| concorrência | Dados dois workers disputando a mesma entrega, quando ambos terminam, então exatamente uma chamada ao provider, um `delivered` e um `attempt_count` | `NotificationWorkerTests.Concurrent_worker_services_produce_one_delivery_and_one_provider_call` | coberto |

## Limites mantidos

Nenhuma regra Meta, webhook, inbound ou interpretação de mensagem no worker. O
retry tem dono único (failure policy do Wolverine); timeout é resultado
ambíguo terminal, nunca retry. Envelope, headers e exceção são redigidos antes
da DLQ com tipo de erro estável (`notification_delivery_invalid`,
`notification_delivery_retry_exhausted`, `notification_delivery_unexpected`,
`notification_delivery_provider_missing`). Métricas usam somente canal,
outcome e motivo estáveis. A reconciliação de ambíguos fica para a fatia Meta
(#28).
