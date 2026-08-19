# Runbook — WhatsApp inbound imagem → OCR → IA (#140 subsete)

## Visão

Uma mensagem recebida **com imagem** no webhook (`POST /foundation/notifications/whatsapp/webhook`,
assinatura HMAC do W3) é deduplicada em `notifications.inbound_whatsapp_messages`, publicada na
fila durável `whatsapp-inbound` e processada pelo WorkerHost: download da mídia →
canonicalização (strip de metadados, tetos de pixel/byte) → OCR pelo perfil de visão →
um turno único de IA → resposta de texto ao remetente (janela 24h aberta pelo inbound).
Outros tipos de mensagem continuam ignorados (fora do corte).

## Variáveis de ambiente

| Variável | Obrigatória | Papel |
| --- | --- | --- |
| `META_WHATSAPP_*` | conforme W3 | webhook (assinatura/challenge) + envio da resposta |
| `InboundWhatsAppAi:Text:Endpoint` | com IA | endpoint OpenAI-compatible do perfil de texto |
| `InboundWhatsAppAi:Text:ApiKey` | com IA | chave do perfil de texto (cofre, nunca log) |
| `InboundWhatsAppAi:Text:Model` | com IA | modelo de texto |
| `InboundWhatsAppAi:Vision:Endpoint/ApiKey/Model` | com IA | perfil de visão (OCR) |
| `InboundWhatsAppAi:Vision:AllowImageInputs` | com IA | deve ser `true` |

Tudo-ou-nada: sem os dois perfis o worker sobe e cada imagem falha fechada com
`ai_unconfigured`; presente-porém-parcial derruba o boot (`ValidateOnStart`).

## Provisionamento

1. Migração `20260821120000_AddNotificationMetaInbound` (funções owner, grants runtime/worker).
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

- **Fila acumulando `pending`**: worker sem consumo — verificar grants
  (`wolverine_queue_whatsapp_inbound`) e listener no log do WorkerHost.
- **Tudo `ai_unconfigured`**: perfis ausentes no worker (não no ApiHost).
- **Resposta duplicada após crash**: at-least-once documentado — o `complete` é idempotente
  pelo `reply_message_id`, mas um crash entre envio e finalize pode reenviar uma vez.

## Corte (o que ficou de fora)

Inbound de texto/áudio, histórico multi-turno, supervisão humana, templates de resposta e
moderação dedicada (guardrails #136 segue em PR próprio). Webhook real em ambiente de teste
da Meta não faz fan-out de eventos (constatado ao vivo em 2026-08-19); a receita de
produção é idêntica à validada peça por peça.
