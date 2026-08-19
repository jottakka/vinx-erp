# Casos públicos — issue 26, intenções de notificação e outbox transacional

## Escopo desta fatia

Esta fatia cria o schema `notifications` (intenções, preferências por canal e
templates aprovados), as funções `SECURITY DEFINER` de enfileiramento e
preferência, o serviço produtor transacional e a publicação durável na fila
`notification-delivery`. Não há worker, provider Meta, webhook, rota HTTP nova
ou produtor de evento de negócio nesta entrega; Paguai permanece rascunho e
nenhum alerta é fabricado.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado destinatário membro com opt-in WhatsApp e templates aprovados, quando o produtor confirma a transação, então há uma intenção por canal em `pending_delivery`, uma mensagem opaca por entrega e auditoria `notification.intent.enqueue` | `NotificationIntentOutboxTests.Eligible_whatsapp_event_creates_both_intents_and_opaque_messages_after_commit` | coberto |
| sucesso | Dado evento somente in-app, quando enfileira, então há uma única intenção e uma única mensagem | `NotificationIntentOutboxTests.In_app_only_event_creates_single_intent_and_message` | coberto |
| validação | Dado destinatário sem opt-in WhatsApp, quando enfileira, então o WhatsApp é suprimido e o alerta in-app obrigatório permanece com mensagem própria | `NotificationIntentOutboxTests.Opted_out_recipient_keeps_mandatory_in_app_alert_without_whatsapp_message` | coberto |
| validação | Dado template WhatsApp não aprovado, quando enfileira, então nada é gravado: sem intenções, mensagens ou auditoria | `NotificationIntentOutboxTests.Unapproved_whatsapp_template_rejects_enqueue_without_rows_or_messages` | coberto |
| validação | Dado comando com evento vazio ou deep-link não relativo, quando enfileira, então rejeita antes de qualquer efeito | `NotificationIntentOutboxTests.Invalid_command_parameters_reject_without_side_effects` | coberto |
| acesso | Dado destinatário membro somente de outra empresa, quando enfileira para a empresa do evento, então nega fechado sem linhas ou mensagens | `NotificationIntentAccessTests.Recipient_from_another_company_is_rejected_without_rows_or_messages` | coberto |
| acesso | Dado role `vinx_runtime`, quando tenta SELECT/INSERT direto nas tabelas de intenção, então nega; somente `EXECUTE` nas funções existe, e o worker não recebe privilégio no schema | `NotificationIntentAccessTests.Runtime_role_cannot_access_intent_tables_directly` | coberto |
| acesso | Dado ator tentando alterar a preferência de outro destinatário, quando chama o serviço, então nega self-service sem gravar a preferência | `NotificationIntentAccessTests.Preference_change_for_another_user_is_denied` | coberto |
| estado | Dado falha de persistência do envelope antes do commit, quando enfileira, então a transação reverte intenções, mensagens e auditoria | `NotificationIntentStateTests.Envelope_persistence_failure_before_commit_rolls_back_intents` | coberto |
| estado | Dado o mesmo evento enfileirado novamente, quando o serviço responde, então retorna a entrega existente sem segunda mensagem, sem auditoria extra e sem tocar status ou tentativas | `NotificationIntentStateTests.Replayed_event_does_not_duplicate_deliveries_or_touch_status` | coberto |
| estado | Dado opt-out após o primeiro enfileiramento, quando um novo evento chega, então o novo WhatsApp é suprimido, o in-app permanece obrigatório e a entrega anterior preserva status e timestamp | `NotificationIntentStateTests.Opt_out_after_first_enqueue_blocks_new_whatsapp_events_but_preserves_existing` | coberto |
| estado | Dado storage Wolverine não provisionado, quando o host sobe, então nenhuma tabela da fila `notification-delivery` ou do outbox é criada em runtime | `NotificationIntentStateTests.Startup_without_provisioned_storage_does_not_create_notification_storage` | coberto |
| concorrência | Dados dois produtores simultâneos para o mesmo evento/canal/template, quando ambos terminam, então exatamente uma entrega por canal, uma mensagem por entrega e replays equivalentes | `NotificationIntentConcurrencyTests.Concurrent_duplicate_events_produce_one_delivery_per_channel` | coberto |
| concorrência | Dados eventos distintos para o mesmo destinatário, quando ambos enfileiram, então entregas e mensagens independentes | `NotificationIntentConcurrencyTests.Distinct_events_for_same_recipient_create_separate_deliveries` | coberto |

## Limites mantidos

A mensagem e as tabelas contêm somente identificadores opacos: sem telefone,
e-mail, corpo, payload de negócio ou segredo; o deep-link é selector relativo.
O opt-in é revalidado dentro da função no momento do enfileiramento e o canal
in-app não é controlável por preferência. O status permanece monotônico
(`pending_delivery`) e sem caminho de atualização nesta fatia. O WorkerHost, o
adapter Meta e os webhooks ficam nas fatias #48 e #28; nenhum produtor de
evento de negócio é conectado aqui.
