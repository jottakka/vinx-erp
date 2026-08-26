# Casos testados — recibo Telegram inbound

`completed` abaixo significa somente recibo técnico da fila. Não confirma
leitura, resposta humana ou efeito de negócio; a confirmação continua sendo
decisão explícita do servidor.

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso: webhook público admite; WorkerHost real conclui sem HTTP/IA | `TelegramInboundWorkerTests::Public_webhook_and_real_worker_complete_an_admitted_pending_row_as_queue_receipt` | Detecta quebra entre ApiHost, fila durável, listener público e recibo SQL | coberto |
| Validação/erros: GUID vazio, linha ausente, SQLSTATE inesperado e Npgsql transitório com 1 s/5 s/30 s | `TelegramInboundWorkerTests::Real_worker_persists_an_invalid_envelope_as_a_redacted_telegram_dead_letter`; `::Real_worker_consumes_a_missing_row_envelope_as_a_noop_without_dlq`; `::Unexpected_database_failure_is_dead_lettered_once_without_sensitive_content`; `::Transient_database_failure_retries_after_one_five_and_thirty_seconds_then_redacts_dlq` | Detecta classificação errada, retry infinito/curto, DLQ indevida e vazamento de UUID, header ou detalhe da falha | coberto |
| Acesso: somente worker conclui e usa as duas tabelas Telegram sem `CREATE` | `TelegramInboundWorkerTests::Runtime_cannot_complete_a_pending_row_and_only_worker_has_function_execute`; `WolverineStorageScriptTests::Setup_script_applies_wolverine_storage_and_runtime_grants`; `WorkerHostSecurityContractTests::Worker_role_cannot_read_api_envelopes_or_directly_mutate_foundation` | Detecta ampliação de função, schema ou grants da fila | coberto |
| Estado/exceções: ausente e concluído são no-op; resolução fechada retém evidência; cancelamento propaga | `TelegramInboundWorkerTests::Closed_and_missing_messages_return_stable_noop_receipts`; `::Real_worker_keeps_a_non_admitted_webhook_row_as_pending_evidence`; `::Public_handler_propagates_cancellation_without_recording_a_receipt` | Detecta conclusão indevida, perda da evidência ou sucesso inventado | coberto |
| Concorrência: três envelopes e dois workers produzem uma transição e dois replays | `TelegramInboundWorkerTests::Racing_real_worker_envelopes_report_one_transition_and_replays_are_noops` | Detecta segunda transição ou segundo efeito | coberto |

O storage permanece pré-provisionado: o WorkerHost usa `AutoCreate.None`; o
contrato público `WorkerHostIngressTests::Worker_host_fails_closed_when_storage_is_not_provisioned`
detecta criação implícita ou startup permissivo.
