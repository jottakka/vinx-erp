# Módulo Notifications

O módulo `Vinx.Erp.Notifications` possui a fronteira de providers de entrega
por canal consumida pelo WorkerHost. A intenção, preferências, templates,
funções SQL e o serviço produtor permanecem na Foundation (ver
`docs/modules/foundation.md`, seção de notificações); este módulo não possui
schema próprio nem migrations.

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
- Contrato de provider: `INotificationProvider.DeliverAsync(deliveryId, ct)`
  retornando classificação estável; timeout vira `Ambiguous` e não lança
  transitiva.

## Deletion strategy

O módulo é stateless; não possui ciclo de vida de dados. Entregas e DLQ
seguem as estratégias da Foundation e do Wolverine.

## Migration notes

Sem migrations próprias. Os estados do worker, funções
`accept/claim/finalize/release_notification_delivery` e os grants para
`vinx_worker` vivem na migration `AddNotificationDeliveryWorker` da
Foundation; o storage técnico Wolverine continua provisionado somente por
deploy (`db-apply`), nunca em startup.
