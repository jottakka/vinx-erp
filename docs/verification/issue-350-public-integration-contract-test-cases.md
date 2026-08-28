# Casos verificados — contrato Telegram inbound #350

Esta correção limita a asserção de catálogo à fronteira do runtime: a função
de recibo existe legitimamente para o worker e não é uma entry point do runtime.

| Intenção | Evidência pública | Garantia observada |
|---|---|---|
| Acesso e estado | `TelegramInboundAdmissionTests.Runtime_has_only_the_runtime_secure_definer_functions_and_no_direct_inbound_table_access` | O runtime executa somente as funções de resolução e admissão, sem DML direto na evidência inbound. A função de recibo legítima deixa de ser tratada como ausente. |
| Acesso | `TelegramInboundWorkerTests.Runtime_cannot_complete_a_pending_row_and_only_worker_has_function_execute` | O runtime recebe `42501` ao tentar concluir a linha; somente `vinx_worker` tem `EXECUTE` no recibo. |
| Concorrência | `TelegramInboundWorkerTests.Racing_real_worker_envelopes_report_one_transition_and_replays_are_noops` | Envelopes repetidos preservam uma transição de recibo e replays sem efeito adicional. |
