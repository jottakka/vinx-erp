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

## Inbound imagem → OCR → IA (#140 subsete)

`Providers/Meta/InboundWhatsAppInteractionService` orquestra o subsete
automatizado do #140: o webhook (mesmo endpoint do W3) passa a ler também
`messages[]` e aceita somente `type=image` com id de mídia; o ingress
(`InboundWhatsAppIngressService` no ApiHost) deduplica por `message_id`
(`notifications.record_inbound_whatsapp_message`) e publica
`InboundWhatsAppImageReceived` na fila durável `whatsapp-inbound` na mesma
transação (padrão do outbox transacional). O worker faz claim idempotente
(`claim/complete/fail_inbound_whatsapp_message`), baixa a mídia em duas fases,
canonicaliza (EXIF descartado, tetos de pixel/byte), transcreve pelo perfil
de visão (`DocumentVisionOcr` do módulo AI — conteúdo sempre tratado como
dado), compõe um turno único no perfil de texto e responde em texto na
janela de 24h (`MetaWhatsAppReplyMapper`, cap 4096). Perfis `InboundWhatsAppAi`
são tudo-ou-nada: ausentes, cada imagem falha fechada (`ai_unconfigured`);
parciais derrubam o boot. Falhas terminais carregam motivo estável
(`failure_reason`); transitivas seguem retry 1s/5s/30s e DLQ redigida
(`whatsapp_inbound_*`). Runbook: `docs/runbooks/whatsapp-inbound.md`.

## Deletion strategy

O módulo é stateless; não possui ciclo de vida de dados. Entregas e DLQ
seguem as estratégias da Foundation e do Wolverine.

## Migration notes

Sem migrations próprias. Os estados do worker, funções
`accept/claim/finalize/release_notification_delivery` e os grants para
`vinx_worker` vivem na migration `AddNotificationDeliveryWorker` da
Foundation; o storage técnico Wolverine continua provisionado somente por
deploy (`db-apply`), nunca em startup.
