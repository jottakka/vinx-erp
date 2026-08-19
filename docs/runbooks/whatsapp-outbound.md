# Runbook — WhatsApp Cloud API outbound (piloto de alertas de saída)

Sem valores reais neste documento; credenciais vivem exclusivamente no secret
store do ambiente.

## Configuração (por ambiente)

| Variável | Conteúdo |
|---|---|
| `META_WHATSAPP_PHONE_NUMBER_ID` | Phone number id do WABA (números, não segredo) |
| `META_WHATSAPP_ACCESS_TOKEN` | Token de acesso da Meta (segredo) |
| `META_WHATSAPP_API_BASE_URL` | Base do Graph (padrão `https://graph.facebook.com`) |
| `META_WHATSAPP_WEB_APP_BASE_URL` | Base pública da web app para deep-links |
| `META_WHATSAPP_APP_SECRET` | App secret para validar `X-Hub-Signature-256` (segredo) |
| `META_WHATSAPP_WEBHOOK_VERIFY_TOKEN` | Verify token do cadastro do webhook (segredo) |

ApiHost e WorkerHost validam as options em `ValidateOnStart`: faltando ou
malformada, o host não sobe (falha fechada).

## Provisionamento

1. Segredos no secret store; nunca em `appsettings`, variável de commit ou log.
2. `scripts/setup-wolverine-storage.sh` inalterado (nenhum DDL em runtime).
3. Migrations Foundation aplicadas pelo migrador (tabelas `notification_*`).
4. Na Meta: cadastre o webhook `GET /foundation/notifications/whatsapp/webhook`
   (challenge) e o `POST` do mesmo caminho; o token informado deve ser o
   `WEBHOOK_VERIFY_TOKEN`.
5. Templates: insira em `notifications.notification_templates` o par
   `(template_key, meta_template_name, meta_language)` por empresa, aprovado
   na Meta e com `is_approved = true`.

## Operação

- Destinatários cadastram o número E.164 via
  `notifications.set_recipient_contact` (self-service); troca de número zera
  `is_validated`.
- Estados do worker: `pending_delivery → pending_provider →
  provider_in_flight → delivered|provider_failed|provider_ambiguous`.
  `provider_ambiguous` aguarda reconciliação consultando o painel da Meta;
  nunca reenvie às cegas.
- 5xx/rate-limit repetem 3 vezes (1s/5s/30s) e vão para a DLQ redigida com
  tipo `notification_delivery_retry_exhausted`; replay da DLQ reivindica nova
  tentativa.
- Webhook: eventos em `notifications.notification_provider_events` são
  insert-only por `(message_id, event_type)`; `read` é apenas telemetria.

## Incidentes

| Sintoma | Ação |
|---|---|
| Host não sobe com erro de options | Verificar secret store; nada é logado com valor |
| Muitos `provider_ambiguous` | Checar latência/timeout do Graph no painel; reconciliar manualmente |
| `provider_failed` com `delivery_target_missing` | Destinatário sem número cadastrado; orientar cadastro |
| Webhook retornando 401 | `APP_SECRET` divergente entre ambiente e app da Meta |
| Eventos não chegando | Conferir cadastro do webhook e verify token na Meta |

## Corte deste piloto

Somente alertas de saída com template aprovado. Não há inbound, bot,
interpretação de mensagem, anexos, confirmação financeira nem decisão médica
via WhatsApp.
