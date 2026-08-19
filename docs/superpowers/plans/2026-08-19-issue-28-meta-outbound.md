# [Notifications] Meta Cloud API outbound (#28 — fatia W3) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps com checkbox (`- [ ]`).

**Goal:** adapter WhatsApp Cloud API para o piloto de alertas de saída: número previamente validado, WABA/phone por secret store, template aprovado, envio pelo worker, webhook de status assinado e deduplicado — sem inbound, bot ou interpretação de mensagem.

**Architecture:** o módulo `Vinx.Erp.Notifications` ganha `Providers/Meta` (options `ValidateOnStart`, client HTTP sem redirect, mapper de template mínimo e provider `INotificationProvider` para o canal `whatsapp`). O destino (telefone E.164) nunca sai do banco para envelope/log: o provider busca no momento do envio por função estreita `get_notification_delivery_target` (somente `provider_in_flight` com token correspondente). O webhook do ApiHost verifica `X-Hub-Signature-256` (HMAC-SHA256 do corpo bruto com o app secret) **antes de qualquer parse**, grava eventos deduplicados por `(message_id, event_type)` e não correlaciona nada ao negócio. Retry/DLQ ficam no contrato #48 (client apenas classifica; 5xx/rate-limit lança transitiva).

**Tech Stack:** idem W1/W2; sem pacote novo (HTTP nativo + HMAC `System.Security.Cryptography`); testes com `HttpMessageHandler` fake.

## Global Constraints

- Base: branch `codex/issue-48-notification-worker` (201e2e9, W2) → branch `codex/issue-28-meta-outbound`; PR com base no branch W2 (re-alvejar após merge do #248).
- **Corte obrigatório:** somente número validado, template aprovado, envio pelo worker, status assinado e dedup. FORA: inbound, bot, interpretação, agente via WhatsApp, PDF/imagem recebidos, anexos, confirmação financeira, aprovação, decisão médica (#140 não é implementado).
- Segredo (access token, app secret, verify token) só via env/secret store; nunca em argumento, log, teste ou resposta. HTTP client não segue redirect, sem cookies.
- Conteúdo mínimo: somente template aprovado com variáveis mínimas (deep-link); NUNCA valor, dado bancário, documento, dado médico, e-mail ou payload bruto — no envio, no banco, no log, na DLQ.
- Deep-link: base da web app + selector relativo; não autentica por si, sem segredo persistente, sem PII.
- Webhook: assinatura verificada sobre os bytes brutos ANTES do parse; dedup `(message_id, event_type)`; insert-only monotônico; eventos fora de ordem/desconhecidos ignorados sem erro; `read` não é confirmação humana.
- Envio somente pelo worker (nunca request HTTP); classificação: 2xx → Delivered; 4xx permanente → Rejected; 5xx/429 → `NotificationProviderTransientException`; timeout → Ambiguous. Sem retry paralelo no adapter.
- Testes somente com handler HTTP fake; nenhuma chamada externa automática; credenciais reais ficam fora do Git.
- Gate `./scripts/verify-public.sh` EXIT:0 no HEAD exato.
- Estimativa: ~19 arquivos, ~1.300-1.600 linhas manuais — acima do alvo de 800 por coesão (adapter Meta ponta a ponta), justificativa registrada.

## Padrões canônicos

1. Client HTTP + classificação: `src/Foundation/Vinx.Erp.Foundation/Platform/SupabaseAdminOwnerInvitationClient.cs`.
2. Funções estreitas + grants: migração W2 (`20260819120000`) — SET ROLE owner, REVOKE PUBLIC/migrator, GRANT `vinx_worker`/`vinx_runtime` alvo, auditoria com ator do destinatário.
3. Provider no módulo: `InAppNotificationProvider`/`NotificationDeliveryWorkerService` (W2) — provider chamado sem transação.
4. Endpoint anônimo com corpo restrito: leitura de bytes brutos como no `[StrictJsonBody]` (bytes efetivamente lidos, limite 24 KiB), mas sem model binding (corpo bruto é insumo do HMAC).

---

### Task 1: Migração `20260820120000_AddNotificationMetaOutbound`

**Files:** Create `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260820120000_AddNotificationMetaOutbound.cs` (raw SQL, atributos inline).

**Produz (Tasks 2-3 consomem):**
- `notifications.notification_templates` += `meta_template_name text NULL CHECK (meta_template_name IS NULL OR (channel = 'whatsapp' AND is_approved))`, `meta_language text NULL CHECK (meta_language IS NULL OR meta_language ~ '^[a-z]{2}(_[A-Z]{2})?$')` — mapeamento somente para template WhatsApp aprovado. (ALTERs sob `SET ROLE vinx_notifications_owner` + grant `CREATE` transitório como na W2.)
- Tabela `notifications.notification_recipient_contacts` (company_id, user_id, channel CHECK 'whatsapp', phone_number CHECK `'^\+[1-9][0-9]{6,14}$'`, is_validated bool DEFAULT false, updated_at, updated_by_user_id, PK (company_id,user_id,channel)); RLS ENABLE+FORCE + policies owner.
- Tabela `notifications.notification_provider_events` (id uuid PK, message_id text NOT NULL CHECK len 1..200, event_type text NOT NULL CHECK IN ('sent','delivered','read','failed'), status text NULL, received_at DEFAULT clock_timestamp(), UNIQUE (message_id, event_type)); RLS idem; sem PII (somente identificadores Meta e status).
- Função `notifications.set_recipient_contact(p_company, p_user, p_channel, p_phone)` (self-service: ator = p_user; membership; E.164; UPSERT idempotente; auditoria `notification.contact.change` na transição; `EXECUTE` para `vinx_runtime`).
- Função `notifications.get_notification_delivery_target(p_delivery_id, p_attempt_id) RETURNS TABLE(phone_number text, meta_template_name text, meta_language text, deep_link_selector text)` — somente quando `provider_in_flight` E `provider_attempt_id = p_attempt_id` (senão linha `not_found`); `EXECUTE` somente `vinx_worker`; sem auditoria (leitura de envio).
- Função `notifications.record_provider_event(p_message_id, p_event_type, p_status) RETURNS boolean` — `INSERT ... ON CONFLICT (message_id,event_type) DO NOTHING`; retorna se inseriu; valida `p_event_type`; `p_status` truncado/normalizado (sem payload); `EXECUTE` para `vinx_runtime`; auditoria somente em `failed` (motivo estável, sem conteúdo).
- Down: guards (sem contatos/eventos), drop funções/tabelas/colunas.

### Task 2: `Providers/Meta` no módulo

**Files (Create em `src/Modules/Vinx.Erp.Notifications/Providers/Meta/`):**
- `MetaWhatsAppOptions.cs` — `PhoneNumberId`, `AccessToken`, `ApiBaseUrl` (default `https://graph.facebook.com`), `WebAppBaseUrl`, `AppSecret`, `WebhookVerifyToken` + chave de configuração `META_WHATSAPP_*`; validação de formato (URI absoluta HTTPS sem userinfo/fragment, tokens não vazios).
- `MetaWhatsAppClient.cs` — `HttpClientName = "meta-whatsapp"`; `SendAsync(MetaWhatsAppMessage, ct)` → `NotificationProviderResult`/exceções: 2xx com `messages[0].id` → Delivered; 2xx sem id/4xx conhecidos (número inválido, template não aprovado) → Rejected; 429/5xx → `NotificationProviderTransientException`; timeout `TaskCanceledException` por timeout → resultado Ambiguous (não exceção); bearer somente no header `Authorization`; nada de corpo/resposta em exceção ou log.
- `MetaWhatsAppMapper.cs` — `Map(phone, metaTemplate, language, deepLinkSelector)` → `{messaging_product:"whatsapp", to, type:"template", template:{name, language:{code}, components:[{type:"body", parameters:[{type:"text", text: deepLink}]}]}}` — deep-link somente quando selector existir; link final = `WebAppBaseUrl` + selector; sem qualquer outro dado.
- `MetaWhatsAppProvider.cs` — `Channel = "whatsapp"`; `DeliverAsync`: ADO na conexão do DbContext do worker chamando `get_notification_delivery_target`; sem target → Rejected (permanente, sem chamada externa); mapeia e envia; retorna resultado/classificação.
- Registro no `Program.cs` do WorkerHost: options `ValidateOnStart` (bind `META_WHATSAPP__*`), `AddHttpClient("meta-whatsapp")` timeout 10s sem redirect/cookies, `AddScoped<INotificationProvider, MetaWhatsAppProvider>`.

### Task 3: Webhook no ApiHost

**Files:**
- Create `src/Hosts/Vinx.Erp.ApiHost/Notifications/MetaWhatsAppWebhookVerifier.cs` — `Verify(rawBody, header)`: `X-Hub-Signature-256 == "sha256=" + HMACSHA256(AppSecret, rawBody)` em tempo constante (`CryptographicOperations.FixedTimeEquals`); header ausente/malformado → falso.
- Create `src/Hosts/Vinx.Erp.ApiHost/Notifications/MetaWhatsAppWebhookEndpoints.cs` — `MapMetaWhatsAppWebhookEndpoints(this IEndpointRouteBuilder)`:
  - `GET /foundation/notifications/whatsapp/webhook`: `hub.mode=subscribe` + `hub.verify_token` igual ao configurado → `hub.challenge` texto puro; caso contrário `403 {"code":"forbidden"}`.
  - `POST /foundation/notifications/whatsapp/webhook`: anônimo; lê bytes brutos com limite 24 KiB (bytes efetivamente lidos); **verifica assinatura antes de parsear**; inválida → `401 {"code":"invalid_signature"}` sem side effect; válida → parse minimal (entry[].changes[].value.statuses[].{id,status} e `messages[].id` ignorado — inbound fora de escopo); para cada status com event_type válido: `record_provider_event` na transação curta (runtime); tudo deduplicado/fora de ordem → `200 {"code":"accepted"}`; erro de schema conhecido → `400 {"code":"invalid_request"}` sem detalhe.
- Modify `src/Hosts/Vinx.Erp.ApiHost/Program.cs`: options `MetaWhatsAppOptions` `ValidateOnStart` + `app.MapMetaWhatsAppWebhookEndpoints()` (fora do grupo autenticado; corpo bruto, sem `[StrictJsonBody]` de model).

### Task 4: Testes (RED → GREEN)

- `tests/public/Vinx.Erp.Foundation.UnitTests/MetaWhatsAppContractTests.cs` — mapper (payload mínimo exato; sem selector → sem `components`; selector → link base+selector; nada além de `to/template`), verifier (assinatura válida/ausente/alterada um byte), classificação do client (fake handler: 200+id → Delivered; 400 (#131047) → Rejected; 429/500 → transient; timeout → Ambiguous; sem redirect — handler responde 302 → tratado como permanente).
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/MetaWhatsAppWebhookTests.cs` — challenge ok/negado; POST sem assinatura → 401 e zero eventos; POST assinado válido → evento único; replay exato → sem duplicar; fora de ordem/desconhecido → 200 sem efeito; tabela sem PII (somente id/status); corpo > 24 KiB → 413/400 sem leitura além do limite.
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/MetaWhatsAppOutboundTests.cs` — full-stack na infra do W2 (`NotificationWorkerHostFactory` + handler fake do `meta-whatsapp` via `ConfigurePrimaryHttpMessageHandler`):
  1. Destinatário com contato validado + template com meta mapping + opt-in → worker entrega `delivered`, exatamente uma chamada Graph com payload mínimo (assert do corpo fake), auditoria provider.
  2. Sem contato cadastrado → `provider_failed` sem nenhuma chamada externa.
  3. 5xx do Graph → transitiva: 4 tentativas, release entre tentativas, DLQ `retry_exhausted`, sem confirmação falsa.
  4. Timeout do Graph → `provider_ambiguous` com uma chamada.
  5. Template sem meta_name → Rejected sem chamada externa.
- Extensões no `NotificationIntentSeed`: `SeedContactAsync`, `SeedTemplateMetaAsync` (admin, direto) + assert de corpo do fake.

### Task 5: Docs

- `docs/verification/issue-28-whatsapp-outbound-test-cases.md`; `docs/runbooks/whatsapp-outbound.md` (sem valores reais: env vars, provisionamento WABA/db-apply, rotação, resposta a incidentes, limites do corte); `docs/modules/notifications.md` (seção Meta) e `docs/modules/foundation.md` (contatos/eventos/tabelas novas).

### Task 6: Gate + PR

- `./scripts/verify-public.sh` EXIT:0 no HEAD exato → push → `gh pr create --draft --base codex/issue-48-notification-worker` → evidências (PR + issue #28) → ledger.

## Self-review

- Handoff W3 × plano: elegibilidade ✓ (T4: opt-in já W1; número/contato, template meta, sem target → sem chamada); options ValidateOnStart ✓; segredo só env ✓ (bearer header; nada em log/exceção); sem redirect ✓; template/variáveis mínimos ✓ (mapper + testes); deep-link base+selector sem segredo/PII ✓; assinatura antes do parse ✓; dedup message_id+event_type ✓; monotônico insert-only ✓; fora de ordem ignorado ✓; classificação com delegação de retry ao #48 ✓ (sem retry no adapter); só fake handler ✓; runbook sem valores ✓; corte (sem inbound/bot/#140) ✓; envio só pelo worker ✓.
- Estimativa: 19 arquivos < 25; >800 linhas com justificativa.
