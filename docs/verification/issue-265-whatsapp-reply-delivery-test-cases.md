# Casos BDD — barreira de resposta WhatsApp (#265)

## Escopo

Esta fatia garante no máximo uma tentativa automática de `POST` da Meta por
linha inbound. Não promete entrega exactly-once do provider e não adiciona
reconciliação automática, scheduler, broker ou correlação de #264.

| Classe BDD | Dado / quando / então | Evidência automatizada |
| --- | --- | --- |
| Sucesso | Dada imagem admitida com OCR e IA válidos, quando a barreira é confirmada e a Meta devolve `wamid`, então a linha conclui com uma resposta. | `MetaWhatsAppInboundTests.Image_is_transcribed_by_ocr_and_answered_with_an_ai_reply`; `InboundWhatsAppReplyStateFunctionTests.Completion_accepts_only_reply_in_flight_and_replays_the_same_wamid_idempotently` |
| Validação e erros | Dada rejeição definitiva, timeout, transporte incerto, HTTP 408/429/5xx ou resposta 2xx malformada/sem `wamid`, quando a tentativa já começou, então há um POST, a linha fecha como `reply_rejected` ou `reply_ambiguous` e a redelivery não chama nenhum limite externo. | `MetaWhatsAppInboundTests.Post_barrier_outcomes_are_terminal_and_redelivery_does_no_external_work`; `InboundWhatsAppReplyStateFunctionTests.Reply_close_reasons_transition_only_reply_in_flight` |
| Acesso | Dado runtime ou `PUBLIC`, quando tenta executar as funções da barreira ou alterar a linha inbound diretamente, então PostgreSQL nega; somente `vinx_worker` tem a fronteira estreita. | `InboundWhatsAppReplyStateFunctionTests.Reply_barrier_functions_are_worker_only_and_inbound_rows_have_no_runtime_or_public_dml` |
| Estado e exceções | Dado aceite do fake Meta seguido de falha única na confirmação local, quando a mesma linha durável é processada de novo, então há um POST, a linha permanece `reply_in_flight` e a redelivery é bloqueada. | `MetaWhatsAppInboundTests.Accepted_reply_then_process_loss_before_complete_is_not_sent_again_on_redelivery`; `InboundWhatsAppReplyStateFunctionTests.Invalid_transitions_leave_reply_in_flight_unchanged` |
| Concorrência e replay | Dados dois workers ou replays disputando a mesma linha, quando tentam iniciar a resposta, então somente um vence a transição para `reply_in_flight`, sem expor alvo/mídia ao perdedor. | `InboundWhatsAppReplyStateFunctionTests.Concurrent_workers_have_exactly_one_processing_to_reply_in_flight_winner`; `InboundWhatsAppReplyStateFunctionTests.Reply_in_flight_cannot_be_claimed_and_does_not_expose_target_or_media`; `MetaWhatsAppInboundTests.Webhook_replay_processes_the_image_only_once` |

Comandos focados: `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~MetaWhatsAppInboundTests -v minimal` e `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~InboundWhatsAppReplyStateFunctionTests -v minimal`.

Recuperação é manual somente em `reply_in_flight`: evidência autorizada com
`wamid` conhecido permite concluir; sem `wamid`, fecha como `reply_ambiguous`.
`reply_ambiguous` já é terminal e nunca conclui, reseta, reabre ou reenvia para
uma nova tentativa automática. Log seguro: `MetaWhatsAppInboundTests.Ambiguous_transport_logs_one_safe_application_decision_without_provider_exception`.
