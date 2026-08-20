# Plan — WhatsApp inbound image → OCR → AI reply (#140, subsete automatizado)

## Escopo

Fatia stacked sobre o W3 (`codex/issue-28-meta-outbound`, PR #257) que entrega o
subsete automatizado do #140: uma mensagem **inbound com imagem** no webhook do
WhatsApp Cloud API dispara OCR (perfil de visão do #135-B), um turno único de IA
(perfil de texto) e uma **resposta em texto** para o remetente dentro da janela
de 24h aberta pela mensagem recebida.

Ficam fora: inbound de texto/áudio/outros tipos, histórico multi-turno,
moderação humana (supervisão), templates de resposta, retry de negócio além do
DLQ do worker, e webhook real em produção (limitação da WABA de teste constatada
no smoke de 2026-08-19).

## Fluxo

1. **Webhook (ApiHost)**: `POST /foundation/notifications/whatsapp/webhook`
   (assinatura verificada sobre bytes brutos, como no W3) passa a ler também
   `messages[]`; apenas `type=image` com `id` de mídia é aceito. Cada mensagem é
   deduplicada por `message_id` na tabela nova e, quando inédita, publica
   `InboundWhatsAppImageReceived(inboundId)` na fila durável `whatsapp-inbound`
   no mesmo outbox transacional (padrão `NotificationIntentService`).
2. **Worker (WorkerHost)**: handler consome a fila e processa por mensagem:
   `claim` idempotente → download da mídia (Graph `GET /{media_id}` → URL →
   bytes) → `ImageCanonicalizer` (strip de metadados, tetos de pixel/byte,
   rejeição determinística) → `DocumentVisionOcr` (transcrição estrita JSON,
   conteúdo tratado como dado) → turno único no perfil de texto (caption + OCR
   como dados, instrução fixa) → resposta de texto via
   `POST /{phone_number_id}/messages` (`type=text`, cap 4096) → `complete` ou
   `fail` idempotentes.
3. **Falhas**: transitivas (HTTP 5xx/429 do Graph, timeout de rede) →
   `NotificationProviderTransientException` com política Wolverine
   1s/5s/30s e DLQ redigido (padrão W2); permanentes (validação, provider
   rejeitou, OCR sem texto, IA vazia além do fallback) → `fail` terminal com
   `failure_reason` estável; IA não configurada → `fail('ai_unconfigured')`
   (boot segue de pé, fail-closed por mensagem — padrão W3).

## Migração (`20260821120000_AddNotificationMetaInbound`)

- `notifications.inbound_whatsapp_messages`: id PK, message_id UNIQUE,
  wa_id, media_id, caption, status CHECK (`pending|processing|completed|failed`),
  reply_message_id, failure_reason, created_at, replied_at. RLS FORCE + políticas
  owner.
- Funções SECURITY DEFINER owner (search_path fixo, revoke PUBLIC/migrator):
  - `record_inbound_whatsapp_message(...)` → `created|replay` (grant
    `vinx_runtime`; sem auditoria — mesmo racional do `record_provider_event`).
  - `claim_inbound_whatsapp_message(p_inbound_id)` → linha + `claimed`
    (grant `vinx_worker`; `pending→processing`, re-run de `processing` continua,
    terminal retorna `claimed=false`).
  - `complete_inbound_whatsapp_message` / `fail_inbound_whatsapp_message`
    (grant `vinx_worker`; guardam apenas `WHERE status='processing'`).
- Down guarda estado (como W3) e reverte.

## Configuração (all-or-nothing, padrão W3)

- `META_WHATSAPP_*` existente cobre webhook e envio da resposta.
- `InboundWhatsAppAi` (section bind): perfis `Text` e `Vision`
  (`OpenAiCompatibleOptions` do #135-B). Sem perfis → boot ok e
  `fail('ai_unconfigured')` por mensagem. Com perfis incompletos →
  `ValidateOnStart` derruba o boot (misconfig é erro de deploy). Vision exige
  `AllowImageInputs`.
- HttpClients nomeados `whatsapp-inbound-ai-text` / `-vision` (timeout do
  perfil, sem redirect/cookie), construídos por mensagem via `IHttpClientFactory`
  (padrão do `MetaWhatsAppProvider` — grafo DI inline-ável do Wolverine).

## Testes (fakes, zero chamada externa)

- Unit: mapper de resposta (payload + cap 4096); parse de `messages[]`
  (imagem aceita, demais ignorados, campos inválidos ignorados).
- Integração webhook: POST assinado cria linha inédita, replay não duplica,
  não-imagem não cria; envelope publicado na fila `whatsapp-inbound`
  (assert no consumidor worker com fake, padrão W2).
- Integração worker: mídia fake (PNG mínimo) + OCR fake (JSON estrito) + IA
  fake + resposta aceita → `completed` com `reply_message_id`; transitivo →
  retry; permanente → `failed` com reason estável; IA não configurada →
  `failed('ai_unconfigured')`.

## Não-metas / riscos aceitos

- At-least-once: crash entre envio da resposta e `complete` pode duplicar a
  resposta em re-run (documentado; espelha a semântica do outbox).
- Conteúdo do usuário é dado não confiável: nunca vira instrução de sistema
  (caption/OCR entram como dados; instrução fixa no system message).
- Sem histórico de conversa: cada imagem é um turno único.
