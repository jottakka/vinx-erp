# Runbook — WhatsApp inbound e Agente multicanal

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
Se o texto chega primeiro, a imagem sem legenda correlacionada depois segue
imediatamente; imagem com legenda também segue imediatamente. Se a imagem sem
legenda chega primeiro, o texto posterior não antecipa o processamento: o
envelope da imagem conserva o prazo original. Ambiguidade não enfileira
processamento de imagem. O envelope de expiração de texto já exigido pode
permanecer agendado e vira no-op ao encontrar o estado terminal. Replay não
republica; texto sem imagem expira sem IA nem resposta automática.

Após admissão, o canal publica para o Agente somente `InboundMessageId`. O
handler compartilhado relê o registro autorizado, faz claim durável antes de
OCR/provider e chama o mesmo núcleo de Web e Telegram. Documento segue
download, quarentena, scan, `FileRecord` confirmado e contexto OCR efêmero;
mídia, número, texto, prompt e credencial não entram no envelope. A resposta
conserva a barreira de ambiguidade e não confirma Pagaí nem entrega externa.
Se houver queda depois do terminal do ledger e antes da resposta, uma redelivery
recupera somente o ledger correspondente em `completed`, `failed_closed` ou
`ambiguous`, reutiliza o `agent_file_id` confirmado e converge a mesma barreira.
Ledger ausente ou ainda em `claimed`/`*_in_flight` permanece no-op para
reconciliação manual; não há timer, reset automático, novo OCR ou nova chamada
ao modelo.

## Variáveis de ambiente

| Variável | Obrigatória | Papel |
| --- | --- | --- |
| `META_WHATSAPP_APP_SECRET` / `META_WHATSAPP_WEBHOOK_VERIFY_TOKEN` / `META_WHATSAPP_WEB_APP_BASE_URL` | sim | webhook (assinatura/challenge) |
| `META_WHATSAPP_PHONE_NUMBER_ID` | sim, para admissão de mídia | canal estável: o `phone_number_id` do webhook deve ser exatamente este valor |
| `VINX_AGENT_PROVIDER_API_KEY` | sim, para Agente real | credencial somente no ambiente do processo WorkerHost |

Perfil real ausente ou inválido falha fechado; testes automatizados usam o
modelo determinístico. OpenCode Go é só o primeiro perfil real/smoke, sem
tornar o núcleo específico do provider.

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

Áudio inbound, resposta automática a texto isolado, histórico
multi-turno, chat/sessão, supervisão humana,
templates de resposta e moderação dedicada (guardrails #136 segue em PR
próprio). O pipeline de documentos (download, quarentena, MIME/checksum,
ClamAV, OCR e rascunho Pagaí) já está implementado — ver "Documento seguro
(#272 PR2)" e as PRs do #272. Webhook real em ambiente de teste da Meta não
faz fan-out de eventos (constatado ao vivo em 2026-08-19); a receita de
produção é idêntica à validada peça por peça.
