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

## Provider Telegram — contrato compartilhado (#307)

A Foundation aceita o canal opt-in `telegram` sem alterar a semântica do
WhatsApp ou do in-app obrigatório. O template aprovado é texto plano de 1 a
4096 caracteres em `notification_templates.telegram_text`; o contato privado
usa `notification_recipient_contacts.address` com chat ID decimal positivo e
`phone_number` permanece exclusivo do WhatsApp. A função
`notifications.enqueue_notification_intent` revalida o opt-in e o template no
momento do enfileiramento. Quando o opt-in existe, Telegram e in-app são
criados na mesma transação e cada entrega nova publica somente
`NotificationDeliveryRequested(deliveryId)` no outbox; o texto e o endereço
nunca entram no envelope, metadado, log ou métrica.

Alterar o endereço por `notifications.set_recipient_contact` limpa
`is_validated`; repetir o mesmo endereço não altera a linha nem gera auditoria
extra. A validação do chat é externa e controlada por processo administrativo
até que uma vinculação de uso único seja aprovada. Esta fatia não chama a API
do Telegram, não recebe webhook e não cria resposta automática.

## Inbound admissão e identidade (#272 PR1)

O webhook (mesmo endpoint do W3) lê `messages[]` dos tipos `image`, `document`
e `text`, exigindo `metadata.phone_number_id` (canal estável; em deploy de
número único, deve ser exatamente o `META_WHATSAPP_PHONE_NUMBER_ID`
configurado). O ingress (`InboundWhatsAppIngressService` no ApiHost) resolve
a identidade e grava a admissão deduplicada por `(phone_number_id,
message_id)` na mesma transação:

- `IInboundActorResolver`/`SqlInboundActorResolver` chamam
  `notifications.resolve_inbound_whatsapp_actor(wa_id)` (SECURITY DEFINER,
  leitura de `notification_recipient_contacts` + memberships/empresas da
  Foundation), com resultado fechado: `admitted` (exatamente um usuário,
  empresa e membership ativa), `unbound` (sem contato), `unverified`
  (contato sem `is_validated`), `inactive` (membership revogada ou empresa
  excluída) ou `ambiguous` (múltiplas identidades ativas — nunca escolhe).
  Como um contato se torna validado é política de vinculação pendente de
  aprovação; esta fronteira somente lê o estado atual.
- As funções `admit_inbound_whatsapp_media` e `admit_inbound_whatsapp_text`
  persistem a linha com o
  resultado da resolução (`resolution_state` + ids resolvidos + `resolved_at`)
  e classifica a entrega como `created`, `replay` ou `conflict` (messageId
  reaproveitado com payload incompatível — conflito seguro, sem duplicar).
  Texto aceita no máximo 4096 caracteres.

Texto e imagem sem legenda correlacionam por 30 segundos somente com a chave
exata `(phone_number_id, wa_id, user_id, company_id, membership_id)`. Se o texto
chega primeiro, a imagem sem legenda correlacionada depois processa
imediatamente; imagem com legenda também processa imediatamente. Se a imagem
sem legenda chega primeiro, o texto posterior apenas grava o vínculo: o
envelope da imagem conserva seu prazo original. Ambiguidade falha os candidatos
como `correlation_ambiguous` e não enfileira processamento de imagem; o envelope
de expiração de texto já exigido pode permanecer agendado e vira no-op ao
encontrar o estado terminal. Texto isolado fecha como `correlation_timeout`, sem
IA ou resposta. Replays não publicam novamente. Wolverine usa o outbox e seu
agendamento durável existentes; documentos continuam em
`whatsapp-inbound-documents`. Linhas não admitidas são somente evidência.

## Inbound extração tipada de fatura (#272 PR3)Após a quarentena limpa, o mesmo processamento extrai o candidato tipado:
PDF usa a camada de texto local (`DocumentTextExtractor`); sem camada =
`no_text_layer`. Um turno único do perfil de texto (`InboundInvoiceExtractor`,
contrato JSON estrito de 4 chaves, transcrição/legenda como dado rotulado)
produz `ExtractedInvoiceData` e o fechamento determinístico em
`notifications.record_inbound_whatsapp_invoice_candidate` grava
`candidate` (beneficiário + valor como texto da gramática decimal do Pagaí +
BRL + vencimento + número opcionais) ou `needs_review` com motivo estável
(`missing_amount`, `invalid_amount`, `missing_beneficiary`,
`missing_due_date`, `no_text_layer`, `extraction_unavailable`). Sem defaults
silenciosos e sem arredondamento; needs_review não é falha da mensagem. O
pre-check `notifications.inbound_document_actor_is_allowed` fecha
membership/capability antes de qualquer download ou chamada de IA. Sem
mutação Pagaí e sem resposta automática nesta fatia (PR4 e decisões do #272).
A integração Pagaí (#272 PR4) é sempre requerida por um ator autenticado:
`POST /paguai/requests/from-document` lê o candidato por
`notifications.read_inbound_whatsapp_invoice_candidate` (escopo de membership),
revalida a gramática do valor e cria o rascunho único por documento
(`source_inbound_message_id` UNIQUE em `paguai.requests`; idempotente por ator
com RequestId = id da admissão), com title/description/proofRequirement
explícitos. Manifesto: `docs/verification/272d-paguai-draft-from-document-test-cases.md`.

## Inbound documento seguro (#272 PR2)

`Providers/Meta/InboundWhatsAppDocumentService` processa a fila
`whatsapp-inbound-documents` com o claim dedicado
`notifications.claim_inbound_whatsapp_document` (recusa linhas que não são
documentos admitidos): download pela `MetaWhatsAppMediaClient` com o teto de
bytes do `DocumentUploadPolicy` (25 MiB), allowlist de MIME
(pdf/jpeg/png), parse e teto de 20 páginas de PDF pelo `DocumentTextExtractor`
(PdfPig), scan antimalware pelo `IMalwareScanner` (ClamAV; sem endpoint o
worker sobe fail-closed com `BlockedMalwareScanner`) e upload server-side em
quarentena privada (`FileStorage.UploadObjectAsync`, chave
`quarantine/whatsapp/{companyId}/{inboundId}`, container privado do Supabase;
sem configuração o worker sobe com `BlockedFileStorage`). O registro final é
atômico em `notifications.complete_inbound_whatsapp_document` (SECURITY
DEFINER): re-verifica membership ativa e capability `foundation.files.upload`
(company) do remetente resolvido, insere
`notifications.inbound_whatsapp_documents` (ids, MIME, bytes, checksum
sha256, páginas, veredito do scan, provider key) e audita
`notification.whatsapp_document.admitted` com identificadores opacos. Falhas
terminais usam motivos estáveis: `media_unavailable`, `document_rejected`
(MIME/bytes/páginas/malware), `document_scan_unavailable`,
`document_storage_unavailable`, `capability_missing`. Sem OCR, sem extração e
sem resposta automática nesta fatia (PRs seguintes). Grant de DML da fila em
`infra/database/grant-wolverine-runtime.sql` (por nome, inclui
`wolverine_queue_whatsapp_inbound_documents`).

## Inbound imagem → OCR → IA (#140 subsete)

`Providers/Meta/InboundWhatsAppInteractionService` orquestra o subsete
automatizado do #140: após admissão e eventual correlação, o ingress agenda ou
publica `InboundWhatsAppImageReceived` na fila durável `whatsapp-inbound`. O
worker faz claim idempotente,
baixa a mídia em duas fases, canonicaliza (EXIF descartado, tetos de
pixel/byte), transcreve pelo perfil de visão (`DocumentVisionOcr` do módulo AI
— conteúdo sempre tratado como dado), compõe exatamente um turno de sistema e
um turno de usuário. Texto correlacionado, legenda e OCR são blocos opcionais,
rotulados e não confiáveis; não há histórico, chat ou sessão. A resposta segue
na janela de 24h (`MetaWhatsAppReplyMapper`, cap 4096).

Imediatamente antes do único `POST` automático à Meta, a função
`begin_inbound_whatsapp_reply` move a linha de `processing` para
`reply_in_flight` e confirma essa barreira. Assim, cada inbound tem no máximo
uma tentativa automática de provider; não é uma promessa de entrega exactly-once
da Meta. `2xx` com `wamid` conclui a linha; rejeição definitiva fecha com
`reply_rejected`; timeout, transporte incerto, 408/429/5xx, 2xx malformado ou
incerteza local posterior fecham com `reply_ambiguous`. Redelivery ou corrida
não fazem outro POST. Perfis `InboundWhatsAppAi` são tudo-ou-nada: ausentes,
cada imagem falha fechada (`ai_unconfigured`); parciais derrubam o boot.
Falhas antes da barreira preservam retry 1s/5s/30s e DLQ redigida
(`whatsapp_inbound_*`). Runbook: `docs/runbooks/whatsapp-inbound.md`; matriz:
`docs/verification/issue-265-whatsapp-reply-delivery-test-cases.md`.

## Deletion strategy

O módulo é stateless; não possui ciclo de vida de dados. Entregas e DLQ
seguem as estratégias da Foundation e do Wolverine.

## Migration notes

Sem migrations próprias. Os estados do worker, funções
`accept/claim/finalize/release_notification_delivery` e os grants para
`vinx_worker` vivem na migration `AddNotificationDeliveryWorker` da
Foundation; o storage técnico Wolverine continua provisionado somente por
deploy (`db-apply`), nunca em startup.
