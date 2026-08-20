# Módulo Notifications

O módulo `Vinx.Erp.Notifications` possui a fronteira de providers de entrega
por canal consumida pelo WorkerHost. A intenção, preferências, templates,
funções SQL e o serviço produtor permanecem na Foundation (ver
`docs/modules/foundation.md`, seção de notificações); este módulo não possui
schema próprio nem migrations.

## Inbox in-app (#94)

A leitura da inbox é pessoal por membership ativa, não por capability: um
intent in-app é visível quando `channel = 'in_app'`, `status = 'delivered'`,
o destinatário é o próprio ator autenticado e a empresa de origem segue
presente em `foundation.current_actor_memberships()` (membership e empresa
sem exclusão). Revogação de membership ou exclusão da empresa remove o item
da lista e do contador na consulta seguinte, sem mutar a linha.

O estado de leitura vive na própria linha de intenção (`read_at`,
`read_by_user_id`), com CHECKs que garantem par preenchido, canal `in_app`,
leitor igual ao destinatário e `status = 'delivered'`. A marcação é
idempotente: somente a primeira transição efetiva grava e audita
`notification.inbox.read` (identificadores opacos, sem corpo/template);
repetição retorna o mesmo resultado sem nova auditoria.

Três funções `SECURITY DEFINER` no schema `notifications` (dono
`vinx_notifications_owner`, EXECUTE somente para `vinx_runtime`) compõem a
fronteira: `list_inbox_notifications` (keyset `(created_at DESC, id DESC)`,
janela `limit + 1`, cursor tipado decodificado no serviço C#),
`count_unread_inbox_notifications` e `mark_inbox_notification_read`. O
escopo de empresa é recalculado no servidor a cada chamada; nenhum
parâmetro do cliente amplia o conjunto visível. A migration
`AddNotificationInboxReadState` concede EXECUTE de
`foundation.current_actor_memberships()` ao owner de notificações via
`vinx_authorization_owner`.

A resposta HTTP expõe somente campos allowlistados (`id`, `companyId`,
`source`, `event`, `createdAt`, `readAt`, `action`); `source`/`event`/`action`
vêm de mapas fechados por chave completa em `NotificationInboxPresentation`
— chaves desconhecidas rendem `Sistema`/`null`/`null` e nunca ecoam
`event_key` ou `deep_link_selector` brutos. Estados pendentes ou falhos do
worker não listam nem são marcáveis.

## Ownership

- Fronteira `INotificationProvider` (`Delivered|Rejected|Ambiguous`,
  `NotificationProviderTransientException` transitiva e ausência de provider
  permanente), orquestração do worker (`NotificationDeliveryWorkerService`) e
  métricas `Vinx.Erp.Notifications.Delivery`.
- O WorkerHost é o único host que referencia o módulo nesta fatia; o adapter
  Meta (#28) será adicionado em `Providers/Meta`.

## Company scope

A empresa chega ao worker somente pelo identificador opaco da entrega; o
módulo não lê tabelas de negócio e não carrega destinatário, telefone ou
conteúdo. O isolamento continua garantido pelas funções SECURITY DEFINER da
Foundation e pelo RLS do schema `notifications`.

## Contract version

- Mensagem durável: `NotificationDeliveryRequested(Guid DeliveryId)` —
  somente identificador opaco, congelada em #26.
- Contrato de provider: `INotificationProvider.DeliverAsync(request, ct)`
  com `NotificationDeliveryRequest(DeliveryId, AttemptId)`; o token da
  tentativa autoriza a fronteira estreita a ler somente os dados do canal.
  Classificação estável; timeout vira `Ambiguous` e não lança transitiva.

## Provider Meta (WhatsApp outbound)

`Providers/Meta` implementa o canal `whatsapp`: options `ValidateOnStart`
(segredos só no secret store), client HTTP sem redirect que apenas classifica
(2xx com id → Delivered; 4xx → Rejected; 429/5xx → transitiva delegada ao
retry do worker; timeout → Ambiguous), mapper com template aprovado e no
máximo o deep-link como variável única, e provider que lê telefone/template
por `notifications.get_notification_delivery_target` (somente a tentativa em
curso). O webhook público no ApiHost valida `X-Hub-Signature-256` sobre os
bytes brutos antes de qualquer parse e grava eventos insert-only por
`(message_id, event_type)` sem correlação com o negócio; `read` não é
confirmação humana. Runbook: `docs/runbooks/whatsapp-outbound.md`.

## Deletion strategy

O módulo é stateless; não possui ciclo de vida de dados. Entregas e DLQ
seguem as estratégias da Foundation e do Wolverine.

## Migration notes

Sem migrations próprias. Os estados do worker, funções
`accept/claim/finalize/release_notification_delivery` e os grants para
`vinx_worker` vivem na migration `AddNotificationDeliveryWorker` da
Foundation; o storage técnico Wolverine continua provisionado somente por
deploy (`db-apply`), nunca em startup.
