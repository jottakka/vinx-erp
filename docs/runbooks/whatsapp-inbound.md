# Runbook — WhatsApp inbound admissão, imagem → OCR → IA (#272 PR1, #140 subsete)

## Visão

Uma mensagem recebida **com imagem ou documento** no webhook (`POST /foundation/notifications/whatsapp/webhook`,
assinatura HMAC do W3) é admitida com resolução de identidade fechada: o
`metadata.phone_number_id` deve ser exatamente o número configurado e o `wa_id`
resolve para exatamente um usuário, uma empresa e uma membership ativa
(`admitted`); caso contrário a admissão registra `unbound`, `unverified`,
`ambiguous` ou `inactive` e **não avança**. A admissão é deduplicada por
`(phone_number_id, message_id)`.

Imagens admitidas seguem para a fila durável `whatsapp-inbound` e o WorkerHost:
download da mídia → canonicalização (strip de metadados, tetos de pixel/byte) →
OCR pelo perfil de visão → um turno único de IA → resposta de texto ao
remetente (janela 24h aberta pelo inbound). Documentos admitidos ficam
duráveis sem consumidor nesta fatia (download/quarentena/OCR/Pagaí são os PRs
seguintes do #272; sem resposta automática). Outros tipos de mensagem continuam
ignorados (fora do corte).

## Variáveis de ambiente

| Variável | Obrigatória | Papel |
| --- | --- | --- |
| `META_WHATSAPP_APP_SECRET` / `META_WHATSAPP_WEBHOOK_VERIFY_TOKEN` / `META_WHATSAPP_WEB_APP_BASE_URL` | sim | webhook (assinatura/challenge) |
| `META_WHATSAPP_PHONE_NUMBER_ID` | sim, para admissão de mídia | canal estável: o `phone_number_id` do webhook deve ser exatamente este valor |
| `InboundWhatsAppAi:Text:Endpoint/ApiKey/Model` | com IA | endpoint OpenAI-compatible do perfil de texto |
| `InboundWhatsAppAi:Vision:Endpoint/ApiKey/Model` | com IA | perfil de visão (OCR) |
| `InboundWhatsAppAi:Vision:AllowImageInputs` | com IA | deve ser `true` |

Tudo-ou-nada (IA): sem os dois perfis o worker sobe e cada imagem admitida
falha fechada com `ai_unconfigured`; presente-porém-parcial derruba o boot
(`ValidateOnStart`).

## Estados de admissão (`resolution_state`)

| Estado | Significado | Ação |
| --- | --- | --- |
| `admitted` | exatamente um usuário/empresa/membership ativa | imagem segue ao processamento; documento aguarda PR2 |
| `unbound` | nenhum contato do número no tenant | nada a fazer (remetente desconhecido) |
| `unverified` | contato existe sem `is_validated` | política de vinculação pendente de aprovação |
| `ambiguous` | número com múltiplas identidades ativas | nunca escolher automaticamente; requires decisão |
| `inactive` | membership revogada ou empresa excluída | revalidar vínculo do usuário |

Linhas não admitidas permanecem `pending` com o estado de resolução fechado —
são evidência durável da decisão, não fila de retrabalho.

## Provisionamento

1. Migração `20260821120000_AddNotificationMetaInbound` (funções owner, grants runtime/worker) e
   `20260821130000_AddInboundWhatsAppMediaAdmission` (colunas de admissão,
   `resolve_inbound_whatsapp_actor` e `admit_inbound_whatsapp_media`, grants de
   leitura Foundation → owner de notificações).
2. `db-apply` dos dois hosts cria a fila `whatsapp-inbound` (deploy-time, `AutoCreate.None`).
3. `infra/database/grant-wolverine-runtime.sql` concede DML do worker nas tabelas da fila.

## Motivos de falha estáveis (`failure_reason`)

| Motivo | Significado | Ação |
| --- | --- | --- |
| `ai_unconfigured` | perfis de IA ausentes | configurar perfis e reenviar imagem |
| `media_unavailable` | mídia rejeitada/expirada no Graph | usuário reenvia |
| `media_invalid` | decodificação/limites (pixels/bytes) | usuário reenvia imagem válida |
| `ocr_failed` | OCR sem texto válido (e sem legenda) | usuário reenvia imagem legível |
| `ai_failed` | perfil de texto falhou determinístico | ver logs do provider |
| `reply_rejected` / `reply_ambiguous` | resposta recusada/incerta | ver logs; janela 24h pode ter fechado |

Transitivos (5xx/429/timeout de rede) seguem a política Wolverine 1s/5s/30s e depois DLQ
redigido (`whatsapp_inbound_retry_exhausted`).

## Incidentes

- **Mídia ignorada com `channel_unknown`/`channel_unconfigured`**: o
  `metadata.phone_number_id` do webhook difere do (ou o) `META_WHATSAPP_PHONE_NUMBER_ID`
  não está configurado — conferir o número de negócio do painel da Meta.
- **Remetente legítimo parou de receber resposta**: provavelmente a admissão
  registrou `unverified`/`unbound`/`inactive` — conferir o contato do número
  (`notification_recipient_contacts`) e a membership; a IA nunca decide identidade.
- **Fila acumulando `pending`**: worker sem consumo — verificar grants
  (`wolverine_queue_whatsapp_inbound`) e listener no log do WorkerHost. Linhas
  com `resolution_state <> 'admitted'` em `pending` são admissões fechadas
  (comportamento esperado), não backlog do worker.
- **Tudo `ai_unconfigured`**: perfis ausentes no worker (não no ApiHost).
- **Resposta duplicada após crash**: at-least-once documentado — o `complete` é idempotente
  pelo `reply_message_id`, mas um crash entre envio e finalize pode reenviar uma vez.

## Corte (o que ficou de fora)

Download/quarentena/MIME/checksum/ClamAV de documentos, OCR e extração
tipada, rascunho Pagaí e resposta automática para documentos — PRs seguintes
do #272. Inbound de texto/áudio, histórico multi-turno, supervisão humana,
templates de resposta e moderação dedicada (guardrails #136 segue em PR
próprio). Webhook real em ambiente de teste da Meta não faz fan-out de
eventos (constatado ao vivo em 2026-08-19); a receita de produção é idêntica
à validada peça por peça.
