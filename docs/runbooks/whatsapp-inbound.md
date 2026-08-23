# Runbook — WhatsApp inbound, correlação curta e imagem → IA

## Visão

Uma mensagem recebida com **texto, imagem ou documento** no webhook (`POST /foundation/notifications/whatsapp/webhook`,
assinatura HMAC do W3) é admitida com resolução de identidade fechada: o
`metadata.phone_number_id` deve ser exatamente o número configurado e o `wa_id`
resolve para exatamente um usuário, uma empresa e uma membership ativa
(`admitted`); caso contrário a admissão registra `unbound`, `unverified`,
`ambiguous` ou `inactive` e **não avança**. A admissão é deduplicada por
`(phone_number_id, message_id)`.

Texto e imagem sem legenda do mesmo ator podem formar um par durante 30
segundos. A chave exige canal, remetente, usuário, empresa e membership iguais.
Imagem com legenda ou par único segue imediatamente; imagem sem par é agendada
por 30 segundos; ambiguidade não publica; replay não republica. Texto sem
imagem expira sem IA nem resposta automática.

Imagens processadas seguem pela fila durável `whatsapp-inbound` e o WorkerHost:
download da mídia → canonicalização (strip de metadados, tetos de pixel/byte) →
OCR pelo perfil de visão → um turno único de IA → resposta de texto ao
remetente (janela 24h aberta pelo inbound). O pedido à IA contém um sistema
fixo e um único turno de usuário com blocos opcionais `User text`,
`Image caption` e `OCR transcription`, sempre como dados não confiáveis.
Documentos admitidos ficam
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
| `admitted` | exatamente um usuário/empresa/membership ativa | imagem segue ao processamento; documento segue à quarentena (PR2) |
| `unbound` | nenhum contato do número no tenant | nada a fazer (remetente desconhecido) |
| `unverified` | contato existe sem `is_validated` | política de vinculação pendente de aprovação |
| `ambiguous` | número com múltiplas identidades ativas | nunca escolher automaticamente; requires decisão |
| `inactive` | membership revogada ou empresa excluída | revalidar vínculo do usuário |

Linhas não admitidas permanecem `pending` com o estado de resolução fechado —
são evidência durável da decisão, não fila de retrabalho.

## Documento seguro (#272 PR2)

Documento admitido é processado na fila `whatsapp-inbound-documents`:
download (teto 25 MiB) → allowlist MIME pdf/jpeg/png → parse e teto de 20
páginas (PDF) → scan ClamAV → upload em quarentena privada → registro atômico
com re-check de membership/capability (`foundation.files.upload`) e auditoria.

Variáveis adicionais do worker: `SUPABASE_STORAGE_URL` +
`SUPABASE_STORAGE_SERVICE_ROLE_KEY` (ausentes = `document_storage_unavailable`
fechado) e `MalwareScanner:ClamAvEndpoint` (ausente = `document_scan_unavailable`
fechado).

Motivos terminais de documento (`failure_reason`):

| Motivo | Significado | Ação |
| --- | --- | --- |
| `media_unavailable` | documento rejeitado/expirado no Graph | usuário reenvia |
| `document_rejected` | MIME fora da allowlist, bytes acima do teto, PDF inválido/>20 páginas ou malware detectado | usuário reenvia arquivo válido |
| `document_scan_unavailable` | ClamAV não configurado/indisponível | configurar endpoint e reenviar |
| `document_storage_unavailable` | Supabase Storage não configurado/indisponível | configurar storage e reenviar |
| `capability_missing` | remetente sem `foundation.files.upload` ou membership revogada no processamento | revisar papel do remetente |

## Provisionamento

1. Migrações `20260821120000_AddNotificationMetaInbound`,
   `20260821130000_AddInboundWhatsAppMediaAdmission` (colunas de admissão,
   `resolve_inbound_whatsapp_actor` e `admit_inbound_whatsapp_media`, grants de
   leitura Foundation → owner de notificações) e
   `20260823130000_AddInboundWhatsAppTextImageCorrelation` (texto, prazo,
   correlação, expiração e claims estreitos).
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
| `reply_rejected` | rejeição definitiva do provider | verificar o motivo seguro e a janela de 24h; não reenviar automaticamente |
| `reply_ambiguous` | timeout, transporte/HTTP incerto, 2xx malformado ou conclusão local incerta | seguir reconciliação manual; não reenviar automaticamente |
| `correlation_timeout` | texto não encontrou uma imagem única em 30 segundos | nenhuma resposta automática |
| `correlation_ambiguous` | mais de um candidato compatível | revisar sem escolher automaticamente |

Antes da barreira de resposta, transitivos de download/OCR/IA seguem a política
Wolverine 1s/5s/30s e depois DLQ redigida (`whatsapp_inbound_retry_exhausted`).
Depois de `reply_in_flight`, timeout, transporte incerto e HTTP 408/429/5xx
são terminais `reply_ambiguous`: nunca são retries cegos de provider.

## Barreira de resposta e reconciliação manual

`reply_in_flight` significa que VINX confirmou a intenção de fazer sua única
tentativa automática imediatamente antes do `POST`. Uma redelivery dessa linha
é bloqueada e não baixa mídia, não chama OCR/IA e não faz outro `POST`.

Logs estruturados seguros usam `Operation=whatsapp_inbound_interaction` e
`Module=notifications`, sem telefone, conteúdo, token, prompt ou corpo HTTP:

| Resultado | Significado operacional |
| --- | --- |
| `reply_send_started` | barreira confirmada; uma tentativa automática começou |
| `reply_provider_accepted` | Meta devolveu `wamid`; confirmação local ainda é necessária |
| `reply_confirmed` | `wamid` persistido e linha concluída |
| `reply_ambiguous` | resultado externo ou local não é conclusivo |
| `reply_redelivery_blocked` | redelivery encontrou `reply_in_flight` |
| `reply_duplicate_blocked` | corrida perdeu a barreira antes do provider |

Somente uma linha `reply_in_flight` pode ser concluída pelo operador autorizado:
se houver evidência autorizada de um `wamid` conhecido, conclui com esse `wamid`;
sem `wamid` conhecido, fecha como `reply_ambiguous`. `reply_ambiguous` já é
terminal: não pode concluir, resetar, reabrir, republicar ou reenviar para obter
outro POST automático.

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
- **Texto ficou em `correlation_timeout`**: comportamento esperado após 30
  segundos sem imagem única; não republicar nem responder automaticamente.
- **`correlation_ambiguous`**: manter fail-closed; não escolher por ordem de
  chegada e não enviar conteúdo para IA.
- **Tudo `ai_unconfigured`**: perfis ausentes no worker (não no ApiHost).
- **Resposta pendente após crash**: localizar `reply_in_flight`/`reply_ambiguous`,
  seguir a reconciliação manual acima e preservar a evidência autorizada; a
  redelivery automática é bloqueada para evitar um segundo POST.

## Corte (o que ficou de fora)

Download/quarentena/MIME/checksum/ClamAV de documentos, OCR e extração
tipada, rascunho Pagaí e resposta automática para documentos — PRs seguintes
do #272. Áudio inbound, resposta automática a texto isolado, histórico
multi-turno, chat/sessão, supervisão humana,
templates de resposta e moderação dedicada (guardrails #136 segue em PR
próprio). Webhook real em ambiente de teste da Meta não faz fan-out de
eventos (constatado ao vivo em 2026-08-19); a receita de produção é idêntica
à validada peça por peça.
