# Casos verificados — contratos públicos da integração #350

Esta correção alinha os doubles e a asserção de catálogo ao schema e ao
protocolo já entregues. Não muda migrations, grants, cliente SMTP, worker,
retry ou estados de produção.

| Intenção | Evidência pública reparada | Garantia observada |
|---|---|---|
| Sucesso e concorrência | `EmailNotificationTests.Eligible_recipient_receives_the_approved_email_template` | O fake anuncia somente `LOGIN`, que ele implementa; uma entrega elegível termina `delivered` e captura uma mensagem. A asserção de mensagem única preserva o efeito observável sem introduzir segunda entrega. |
| Erro, estado e concorrência | `EmailNotificationTests.Rejected_authentication_fails_permanently_without_retry` | A rejeição `535 5.7.8` permanece autenticável pelo fluxo `LOGIN`, termina em `provider_failed` e não captura mensagem nem agenda retry. |
| Acesso e estado | `TelegramInboundAdmissionTests.Runtime_has_only_the_runtime_secure_definer_functions_and_no_direct_inbound_table_access` | O runtime conserva apenas as duas funções de admissão/resolução e nenhum DML direto na evidência inbound. A função de recibo legítima não é mais tratada como ausente. |

O contrato complementar
`TelegramInboundWorkerTests.Runtime_cannot_complete_a_pending_row_and_only_worker_has_function_execute`
continua sendo a prova específica de acesso ao recibo: runtime recebe `42501`,
e `vinx_worker` tem `EXECUTE`. As corridas de recibo permanecem cobertas por
`TelegramInboundWorkerTests.Racing_real_worker_envelopes_report_one_transition_and_replays_are_noops`.
