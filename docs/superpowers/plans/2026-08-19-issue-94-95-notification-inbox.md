# [Notifications] Inbox in-app, contador de não lidas e marcação de leitura (#94/#95) — Plano de implementação (rev. 4 pós-4 rodadas Sol: APROVADO COM ALTERAÇÕES)

> **Para executores agênticos:** usar superpowers:executing-plans task a task. Passos com checkbox (`- [ ]`).
> **Revisão Sol:** 1ª BLOQUEADO, 2ª BLOQUEADO, 3ª BLOQUEADO, 4ª **APROVADO COM ALTERAÇÕES** (vereditos em `/tmp/ni94-sol-plan-review-verdict.md`, `/tmp/ni94-sol-review2-verdict.md`, `/tmp/ni94-sol-review3-verdict.md`, `/tmp/ni94-sol-review4-verdict.md`); todos os bloqueios incorporados.

**Goal:** entregar a inbox in-app autorizada (#94: contrato de domínio/API com paginação estável, contador de não lidas e marcação idempotente auditável) e a UI consumidora (#95: rota `/notificacoes`, contador no shell, lista com estados loading/empty/error e ação de marcar como lida), sem alterar outbox, worker, provider, WhatsApp ou Paguai.

**Architecture:** read state como extensão mínima da linha de intenção existente (`read_at`/`read_by_user_id` em `notifications.notification_intents`), sem read model duplicado. Três funções `SECURITY DEFINER` no schema `notifications` (list, count, mark-read) executadas por `vinx_runtime`, dono `vinx_notifications_owner`, ator derivado de `app.user_id`, escopo de empresa recalculado a cada chamada por `foundation.current_actor_memberships()` (já filtra membership e empresa excluídas; aninhada SECURITY DEFINER do `vinx_authorization_owner`). Serviço na Foundation traduz PostgresException em estados; ApiHost expõe HTTP com envelope allowlistado, `Cache-Control: private, no-store` aplicado pelo middleware de BFF (inclui 401/429 antes da action); normalização de `source`/`event`/`action` por allowlist **fechada por chave completa** em C#. UI sem polling: contador buscado no carregamento, focus, após marcação e ao entrar na rota; `companyId` mapeado para nome de empresa do bootstrap.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql (ADO bruto), React 19 + MUI + react-router + vitest, Testcontainers.PostgreSql, xUnit.

## Decisões congeladas (decisões (a)-(g) confirmadas pelo Sol na 1ª rodada; divergências futuras registradas na issue #94)

1. **Fonte de leitura (Sol c):** colunas na tabela existente. `read_at timestamptz NULL`, `read_by_user_id uuid NULL REFERENCES foundation.users(id)`, com CHECKs: `(read_at IS NULL) = (read_by_user_id IS NULL)`; `read_at IS NULL OR channel = 'in_app'`; `read_by_user_id IS NULL OR read_by_user_id = recipient_user_id`; `read_at IS NULL OR status = 'delivered'`. Índices parciais (Sol f): lista `(recipient_user_id, created_at DESC, id DESC) WHERE channel='in_app' AND status='delivered'`; contador `(recipient_user_id, company_id) WHERE channel='in_app' AND status='delivered' AND read_at IS NULL`.
2. **Visibilidade de inbox (Sol a+b):** `channel='in_app'` AND `status='delivered'` AND `recipient_user_id = ator atual` AND `company_id IN (SELECT company_id FROM foundation.current_actor_memberships())`. Nenhum estado pendente/falho lista ou é marcável — testar cada classe de status. Membership ativa (não capability): membro ativo sem capability vê inbox; capability/ID forjado sem membership não vê nada. Novo grant na migration: `GRANT EXECUTE ON FUNCTION foundation.current_actor_memberships() TO vinx_notifications_owner` (Up) com `REVOKE` correspondente (Down). Não usar `app.authorized_company_ids`; sem BYPASSRLS.
3. **Ator/escopo:** `app.user_id` obrigatório (42501 `notification actor is required`); nenhum `companyId`/`recipientUserId` aceito do cliente em query string ou corpo.
4. **Contrato HTTP** (bearer ou cookie BFF; sem `403` — não há capability e lista vazia não revela empresas; `401`/`429` sem corpo):
   - `GET /notifications/inbox?cursor=<opaco>&limit=<1..50>` → `200 { items: [...], nextCursor: string|null }`; `400 invalid_request` (cursor/limite inválidos); `401`; `429`; `503 notifications_unavailable`.
   - `GET /notifications/inbox/unread-count` → `200 { unreadCount: number }`; `401`; `429`; `503`.
   - `POST /notifications/inbox/{deliveryId}/read` → `200 { status: "read" }` idempotente (já lida → mesmo `200`); `400 invalid_request` (falta de CSRF na sessão cookie; `Guid.Empty`); `401`; `404 resource_not_found` (inexistente OU fora do escopo, indistinguível; GUID malformado cai no route constraint `:guid`); `429`; `503`.
   - Item: `{ id, companyId, source, event, createdAt, readAt, action | null }` com `action = { kind, href }` somente por allowlist. Sem nome de empresa na resposta (UI resolve do bootstrap).
5. **Cursor opaco com dono único (Sol 2ª rodada): o C# é o único proprietário do cursor HTTP.** O SQL recebe `p_cursor_created_at timestamptz NULL` + `p_cursor_id uuid NULL` tipados — nenhum parsing/encoding de cursor no SQL. Wire format: base64url canônico SEM padding do ASCII `v1.<micros-epoch-utc>.<uuid-d-lowercase>`; limite de 128 bytes aplicado ao token recebido ANTES do decode; parser estrito (sem espaços, sem versões alternativas, UUID somente formato D minúsculo, sem overflow, encoding canônico — re-encode deve reproduzir o token recebido, senão → `Invalid`→400). Keyset `(created_at DESC, id DESC)` com desempate por `id` para timestamps iguais. Consulta busca `limit + 1` linhas; devolve `limit`; `nextCursor` calculado no C# somente do último item efetivamente retornado; NULL quando não há mais páginas. Cursor não é segredo nem autoridade; sem HMAC. Inserção concorrente à frente do cursor = visível somente após refresh, sem duplicar itens em páginas seguintes.
6. **Allowlist de apresentação fechada por chave completa (Sol):** mapa estático C# `event_key` completo → `(source, event)` (inicialmente conhecidos: nenhum produtor real existe; mapa começa vazio e cresce por PR quando produto adicionar evento) e `deep_link_selector` completo → `(kind, href)` (`/paguai` → `{kind:"paguai", href:"/paguai"}`; `/atendimento` → `{kind:"atendimento", href:"/atendimento"}`). Chave não reconhecida → `source = "Sistema"`, `event = null`; seletor não reconhecido → `action = null`. Nunca ecoar `event_key`/`deep_link_selector`/`template_key` brutos. UI navega só por `action.href` via RouterLink interno.
7. **Marcação idempotente:** `SELECT ... FOR UPDATE` prévio; `UPDATE ... WHERE id = p_delivery_id AND channel='in_app' AND recipient_user_id = ator AND status='delivered' AND read_at IS NULL AND company_id IN (current_actor_memberships())` ; `ROW_COUNT = 1` → auditoria `notification.inbox.read` (só transição efetiva; identificadores opacos; sem corpo/template) na mesma transação; `read_at` já preenchido e visível → `('already_read', false)` sem nova auditoria; invisível/inexistente → `('not_found', false)`. Duas marcações simultâneas → exatamente uma transição auditada.
8. **Concorrência (Sol g + 2ª/3ª rodada):** teste determinístico com DUAS conexões `vinx_runtime` e DUAS transações, ambas chamando a função REAL `notifications.mark_inbox_notification_read()` (nunca `SELECT FOR UPDATE` manual): A inicia tx, `set_config('app.user_id', ...)` transacional, chama a função (retorna `read`), MANTÉM a tx aberta (lock retido); B chama a função na segunda conexão → bloqueia; o teste captura o backend PID de B (`pg_backend_pid()`) e consulta `pg_stat_activity` com `pid = @backendPidB AND wait_event_type = 'Lock'` como prova determinística da espera (padrão já usado em `AuditMigrationTests.cs:350`; sem delay fixo); A commita; B completa → `already_read`. Estado final: um `read_at`, uma auditoria, contador reduzido exatamente uma vez. Teste adicional em nível de serviço com scopes independentes (fumaça; asserção é o estado final). Count concorrente pode observar valor anterior ou posterior (ambos válidos); asserção relevante é o valor final.
9. **Worker/outbox intactos:** nenhuma alteração em claim/retry/DLQ/métricas; in_app continua obrigatório no enqueue.

## Global Constraints

- Branch `codex/notifications-inbox-94-95` a partir de `origin/main` (67ddb8a, contém #243/#248). Nunca escrever em `main`.
- Não ler, listar ou executar `quality/private`.
- Fronteira de arquivos do handoff + adições exigidas pelo Sol: `Program.cs` (somente `AddScoped<NotificationInboxService>` + using, linha única no bloco de registros ~139) e `Identity/BrowserBffRequestMiddlewares.cs` (somente o prefixo `/notifications` na lista de rotas no-store). Não tocar: Paguai module/feature, WorkerHost, `Vinx.Erp.Notifications/` provider, `Directory.Packages.props`, solução, CI, migrations W1/W2.
- Migration raw SQL sem `.Designer.cs` (sem mudança de modelo EF; não disputa snapshot com o agente de pagamento): `20260819140000_AddNotificationInboxReadState.cs`. Down na ordem: DROP FUNCTION ×3 → DROP INDEX ×2 → DROP COLUMN ×2 (colunas por último; índices dependentes saem antes), guard 23514 se existe `read_at NOT NULL`.
- Runtime sem DDL/DML direto: RLS FORCE existente + funções SECURITY DEFINER `SET search_path TO pg_catalog, notifications, pg_temp`; REVOKE FROM PUBLIC e `vinx_migrator`; GRANT EXECUTE TO `vinx_runtime` (somente as três funções novas).
- Sem PII/segredo/payload em resposta, log, auditoria ou métrica; `X-Correlation-ID` do host preservado; `Cache-Control: private, no-store` em TODAS as respostas de `/notifications` via middleware (cobre 401/429 antes da action).
- Sem polling/realtime/push na UI; contador atualiza em mount, focus da janela, após mark-read e ao entrar em `/notificacoes`.
- Testes focados durante o desenvolvimento; `./scripts/verify-public.sh` EXIT:0 serializado antes do push (checar `pgrep -fl dotnet; docker stats --no-stream` antes; cooldowns entre suítes pesadas).
- Estimativa recalculada (Sol 2ª rodada): **dois PRs empilhados**. PR #94 backend ~15 arquivos (~1.100 linhas manuais incl. testes; >800 justificado pela coesão da fatia de contrato; exceção registrada no PR). PR #95 UI ~16 arquivos (Sol 2ª rodada: 6 novos da feature + 5 fontes existentes + 4 testes existentes + manifesto; 17 se `router.test.tsx` mudar), ~850-950 linhas, justificativa própria >800 registrada no PR, base no PR #94, UI só contra contrato congelado. Conflito esperado serial: `docs/contracts/openapi/README.md`, `Program.cs`.
- Ordem das tasks (Sol 2ª/3ª rodada): testes RED (Task 0) ANTES de migration (Task 1)/serviço (Task 2)/controller (Task 3), preservando o fluxo do handoff.
- Nota: `gh auth` inválida reportada pelo Sol é artefato do sandbox read-only dele; o agente principal verificou `gh issue view 94/95` com sucesso nesta sessão e reconfere antes do PR.

## Padrões canônicos (espelhar)

1. Migração raw SQL + SECURITY DEFINER + grants: `20260818120000_AddNotificationIntentOutbox.cs`.
2. Serviço Foundation com ADO bruto + classificação PostgresException: `Notifications/NotificationIntentService.cs`.
3. Leitura autorizada com rollback: `Authorization/AuthorizedCompanySetResolver.ExecuteReadAsync`.
4. Controller + envelope de erro: `Controllers/PaguaiController.cs`; CSRF de mutação: `[BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]` como `Controllers/AtendimentoController.cs:63` (POST sem corpo: NÃO usar `StrictJsonBody`).
5. no-store por rota: `Identity/BrowserBffRequestMiddlewares.cs` (lista `browserSessionRoute`; adicionar prefixo `/notifications`).
6. Seed de teste: `NotificationIntentSeed.cs` (raiz do projeto de testes); leitura HTTP autenticada: `FoundationApiFactory.CreateWithAuthenticatedIdentityAsync` + `CreateClient`.
7. Feature web: `features/paguai/paguai-draft-client.ts` (fluxo antiforgery GET→token→POST `X-CSRF-TOKEN`; extração em helper único fica DEFERIDA — paguai está fora da fronteira deste agente); rota: `shared/routing/app-paths.ts` + `app-route-manifest.tsx`; bootstrap com empresas: `features/auth/session-contract.ts` (`companies[{companyId, companyName}]`).

---

### Task 0 (backend, EXECUTAR PRIMEIRO — Sol 2ª rodada): Testes RED (5 classes BDD, #94)

Escrever os testes ANTES de migration/serviço/controller, contra o contrato congelado; rodar e confirmar RED (falha de compilação/método ausente conta como RED).

**Files (Create, `tests/public/Vinx.Erp.Foundation.IntegrationTests/`):**
- `NotificationInboxSeed.cs` (raiz, ao lado de `NotificationIntentSeed.cs`) — helpers: `CreateCompanyForActorAsync` (empresa+grupo+membership para `factory.ActorUserId`, sem duplicar usuário do ator; template in_app aprovado), enqueue in_app com `RecipientUserId = factory.ActorUserId` via serviço real, avanço até `delivered` chamando `accept/claim/finalize` como `vinx_worker` (conexão dedicada `SET ROLE vinx_worker`), revogação de membership por UPDATE `deleted_at` como admin, segunda empresa para o mesmo ator, e conexão `vinx_runtime` com GUC de ator para os testes de concorrência.
- `NotificationInboxTests.cs` — **Sucesso:** multi-empresa (Alfa+Beta visíveis com companyId de origem; Gama excluída após revogação de membership; empresa excluída também some); paginação estável keyset (limit+1/nextCursor do último item; timestamps iguais desempatam por id; sem duplicar/perder entre páginas; nextCursor null no fim); contador acompanha a lista. **Validação:** cursor malformado/não-versionado/não-canônico (re-encode divergente)/>128 bytes, limit 0/51/não-numérico → 400; `Guid.Empty` no read → 400; deliveryId não-guid → 404 por rota; marcar já lida → mesmo 200 sem segunda auditoria.
- `NotificationInboxAccessTests.cs` — **Acesso:** membro ativo SEM capability vê inbox (membership basta); usuário não membro não vê nada e contador 0; delivery de outra empresa → 404 indistinguível; membership revogada remove da lista E do contador; `vinx_runtime` sem DML direto na tabela (`has_table_privilege` false; funções são a única fronteira); EXECUTE das 3 funções só para `vinx_runtime`; `vinx_worker` sem EXECUTE nelas; grant de `current_actor_memberships` presente para `vinx_notifications_owner` (verificado como admin).
- `NotificationInboxStateTests.cs` — **Estado:** `whatsapp` entregue nunca lista; CADA classe pendente/falha (`pending_delivery`, `pending_provider`, `provider_in_flight`, `provider_failed`, `provider_ambiguous`) não lista nem é marcável (404); falha de auditoria (REVOKE `append_event` do owner) → marcação falha fechada sem `read_at` (transação inteira); leitura não altera colunas de worker (`status`, `attempt_count`, `provider_*` intocados); `already_read` não reaudita; HTTP: bearer e cookie BFF (CSRF válido passa; CSRF ausente → 400), `Cache-Control: private, no-store` presente inclusive em 401/429, **corpo vazio explícito em 401 e 429** (contrato congelado), envelope sem campos extra (`event_key`/`deep_link_selector`/`template_key` ausentes do JSON).
- `NotificationInboxConcurrencyTests.cs` — **Concorrência (determinística, decisão 8):** A e B chamam `notifications.mark_inbox_notification_read()` REAL como `vinx_runtime` em conexões separadas; A retém a transação aberta após `read`; B bloqueia — capturar o backend PID de B via `pg_backend_pid()` e provar a espera consultando `pg_stat_activity` com `pid = @backendPidB AND wait_event_type = 'Lock'` (poll curto com timeout, sem delay fixo); A commita; B completa → `already_read`; final: 1 `read_at`, 1 auditoria, contador reduzido exatamente 1; smoke de serviço: dois scopes independentes `Task.WhenAll` → asserção do estado final (uma transição); count concorrente ao mark: valor antes/depois ambos válidos, valor final correto; inserção concorrente à frente do cursor: sem duplicação nas páginas seguintes.

- [ ] Steps: escrever → `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~NotificationInbox"` → confirmar RED (compilação falha: serviço/controller inexistentes). Commit `test(notifications): add RED inbox suites for list, count and mark-read`.

### Task 1 (backend): Migration `20260819140000_AddNotificationInboxReadState`

**Files:** Create `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260819140000_AddNotificationInboxReadState.cs` (raw SQL, sem Designer).

**Interfaces produzidas (Tasks 2-3 consomem):**
- `notifications.list_inbox_notifications(p_limit integer, p_cursor_created_at timestamp with time zone, p_cursor_id uuid) RETURNS TABLE(id uuid, company_id uuid, event_key text, deep_link_selector text, created_at timestamptz, read_at timestamptz)` — keyset `(created_at DESC, id DESC)` sobre o índice parcial; `p_limit` faixa 1..50 (default 20); cursores NULL = primeira página; parâmetros de cursor são tipados (o C# é o dono do cursor HTTP — nenhum texto de cursor no SQL); busca `p_limit + 1` (o C# decide `nextCursor`); sem ator → 42501 `notification actor is required`; `p_limit` fora da faixa → 22023 `inbox parameters are invalid`; colunas retornadas somente as allowlistadas (sem template_key/status/correlation/provider).
- `notifications.count_unread_inbox_notifications() RETURNS TABLE(unread_count bigint)` — mesma visibilidade + `read_at IS NULL`.
- `notifications.mark_inbox_notification_read(p_delivery_id uuid) RETURNS TABLE(status text, read boolean)` — `read|already_read|not_found`; parâmetro nulo/vazio → 22023; audita `notification.inbox.read` somente na transição efetiva.

- [ ] **Step 1: SQL completo** — grants cross-owner primeiro (Sol 2ª rodada — o migrator é NOINHERIT e só concede via `SET ROLE` do owner da função):
  ```sql
  SET ROLE vinx_authorization_owner;
  GRANT EXECUTE ON FUNCTION foundation.current_actor_memberships()
      TO vinx_notifications_owner;
  RESET ROLE;
  GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
  SET ROLE vinx_notifications_owner;
  -- ADD COLUMN read_at/read_by_user_id + 4 CHECKs + 2 índices parciais
  -- 3 funções plpgsql (ator por GUC; 22023 parâmetros; visibilidade por decisão 2;
  -- FOR UPDATE + UPDATE condicional + auditoria transacional no mark; REVOKE/GRANT por função)
  RESET ROLE;
  REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
  ```
  `Down`: guard 23514 se existe `read_at NOT NULL` → como `vinx_notifications_owner`: DROP FUNCTION ×3, DROP INDEX ×2, DROP COLUMN ×2 → `RESET ROLE` → `SET ROLE vinx_authorization_owner; REVOKE EXECUTE ON FUNCTION foundation.current_actor_memberships() FROM vinx_notifications_owner; RESET ROLE;`.
- [ ] **Step 2:** `dotnet build src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj` → 0 errors.
- [ ] **Step 3:** Commit `feat(notifications): add inbox read state with authorized list, count and mark-read functions`.

### Task 2 (backend): `NotificationInboxService` + allowlist fechada na Foundation

**Files:**
- Create `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationInbox.cs` — contratos de resultado com estado associado (Sol 3ª rodada): `NotificationInboxResult(NotificationInboxState State, NotificationInboxPage? Page = null)`; `NotificationUnreadCountResult(NotificationUnreadCountState State, long UnreadCount = 0)`; `NotificationMarkReadResult(NotificationMarkReadState State)`; enums COM `Unauthorized`: `NotificationInboxState { Listed, Invalid, Unauthorized, Unavailable }`, `NotificationUnreadCountState { Counted, Unauthorized, Unavailable }`, `NotificationMarkReadState { Read, NotFound, Invalid, Unauthorized, Unavailable }` (AlreadyRead normalizado para `Read` — resposta idempotente idêntica); itens `NotificationInboxItem(Id, CompanyId, Source, Event, CreatedAt, ReadAt, Action)`, `NotificationInboxAction(Kind, Href)`, `NotificationInboxPage(Items, NextCursor)`; allowlists estáticas fechadas por chave completa (decisão 6); codec de cursor `v1` base64url canônico sem padding (dono único C#: encode/decode/re-encode-strict ≤128 bytes, decisão 5). `ListAsync`/`CountUnreadAsync`/`MarkReadAsync` declaram esses retornos.
- Create `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationInboxService.cs` — `ListAsync(actor, cursor, limit)`, `CountUnreadAsync(actor)`, `MarkReadAsync(actor, deliveryId)`; transação + `ApplyActorAsync` + ADO bruto; leituras com rollback (padrão resolver); mark com commit; validação precoce C# de limit/cursor/guid (mesma faixa do SQL); classificação `PostgresException`: 42501 ator → `Unauthorized`→401 defensivo; 22023 → `Invalid`→400; resto → log Error `{Operation} {Module} {Result} {Reason}` + `Unavailable`→503.
- Modify `src/Hosts/Vinx.Erp.ApiHost/Program.cs` — `builder.Services.AddScoped<NotificationInboxService>();` junto aos registros existentes (~139) + using.

- [ ] Steps: escrever → `dotnet build Vinx.Erp.sln --no-restore` 0 errors → commit `feat(notifications): add inbox read service with presentation allowlists`.

### Task 3 (backend): `NotificationsController` + no-store + modelos HTTP

**Files:**
- Create `src/Hosts/Vinx.Erp.ApiHost/Controllers/NotificationsController.cs` — `[Route("notifications")]`, `[Authorize]`, rate limit protected; `GET inbox`, `GET inbox/unread-count`, `POST inbox/{deliveryId:guid}/read` com `[BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)]` (sem `StrictJsonBody`: POST sem corpo); mapeamento: Invalid→400 `invalid_request`, Unauthorized→401, Unavailable→503 `notifications_unavailable`, mark NotFound→404 `resource_not_found`, Read→200 `{status:"read"}`; `Guid.Empty` → 400 antes de chamar o serviço.
- Create `src/Hosts/Vinx.Erp.ApiHost/Models/NotificationInboxModels.cs` — `NotificationInboxItemResponse`, `NotificationInboxPageResponse(Items, NextCursor)`, `NotificationUnreadCountResponse(UnreadCount)`, `NotificationMarkReadResponse(Status)`.
- Modify `src/Hosts/Vinx.Erp.ApiHost/Identity/BrowserBffRequestMiddlewares.cs` — adicionar `|| context.Request.Path.StartsWithSegments("/notifications")` na lista `browserSessionRoute` (no-store cobre 401/429/503, não só 200).

- [ ] Steps: escrever → build 0 errors → commit `feat(notifications): expose authorized inbox endpoints with idempotent mark-read`.

### Task 4 (backend): GREEN + regressões

- [ ] `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~NotificationInbox"` → GREEN (corrigir FONTE, nunca afrouxar teste).
- [ ] Regressões: `--filter "FullyQualifiedName~NotificationIntent"` + `~NotificationWorker` + `~Paguai` + `~ArchitectureTests` (se suíte existir no gate focado).
- [ ] Commit `test(notifications): green inbox suites across BDD classes`.

### Task 5 (backend): Docs #94

- [ ] `docs/modules/notifications.md`: seção Inbox (read state, visibilidade por membership, funções, grants incl. `current_actor_memberships`, contrato HTTP resumido, sem PII).
- [ ] `docs/contracts/openapi/README.md`: seção Notifications inbox (rotas, bearer/BFF+CSRF, 200/400/401/404/429/503 sem 403, no-store via middleware, envelope, idempotência, allowlist fechada).
- [ ] `docs/verification/issue-94-notification-inbox-test-cases.md`: tabela BDD × cenário × evidência × situação + Limites mantidos.
- [ ] Commit `docs(notifications): document inbox contract, openapi section and verification cases`.

### Task 6 (backend): Gate parcial + PR #94

- [ ] `./scripts/verify-public.sh` EXIT:0 no HEAD (serializado; load-check antes; flake → cooldown + rerun solo).
- [ ] Push + `gh pr create` (base main) com histórico completo do Sol (1ª BLOQUEADO, 2ª BLOQUEADO, 3ª BLOQUEADO, 4ª APROVADO COM ALTERAÇÕES), BDD, gate, estimativa justificada; comentário na issue #94 com decisões congeladas.

### Task 7 (UI #95): Cliente + provider de contador

**Files:**
- Create `apps/web/src/features/notifications/notifications-client.ts` (+ `.test.ts`) — `fetchInboxPage(cursor?, limit?)`, `fetchUnreadCount()`, `markNotificationRead(id)` (fluxo antiforgery GET→token→POST `X-CSRF-TOKEN` como `paguai-draft-client.ts`; extração em helper compartilhado DEFERIDA — paguai fora da fronteira); tipos `NotificationInboxItem` etc.; erros tipados (`invalid_request`, `resource_not_found`, `notifications_unavailable`, rede) sem vazar detalhes.
- Create `apps/web/src/features/notifications/notifications-provider.tsx` (+ `.test.tsx`) — contexto `{ unreadCount, refreshUnreadCount, markRead }`; busca no mount (bootstrap válido), no focus da janela, após mark-read e ao entrar em `/notificacoes`; sem polling; erro de rede/API não reduz contador nem marca item como lido (critério #95). Reset de estado quando `bootstrap` vira null ou o usuário muda, e guarda contra respostas desatualizadas (abort/ignorar por identidade da requisição) — sem isso o contador da sessão anterior reaparece após logout/login (risco Sol 2ª rodada).

### Task 8 (UI #95): Página `/notificacoes` + rota + contador no header

**Files:**
- Create `apps/web/src/features/notifications/notifications-page.tsx` (+ `.test.tsx`) — lista paginada ("Carregar mais" via `nextCursor`), nome da empresa via `bootstrap.companies[companyId].companyName` (fallback "Empresa"), data relativa, estado lida/não lida, botão "Marcar como lida" com pending/erro+retry/sucesso confirmado pelo servidor (sem otimismo sem rollback), `action.href` como RouterLink allowlistado; estados loading (skeleton), empty, error (Alert + retry); aria-labels, foco visível, teclado, contraste light/dark.
- Modify `apps/web/src/shared/routing/app-paths.ts` — entrada `/notificacoes` (label "Notificações", grupo workspace, navigation true) + testes existentes do catálogo atualizados.
- Modify `apps/web/src/app/app-route-manifest.tsx` — ícone `NotificationsOutlinedIcon` + render `NotificationsPage`.
- Modify `apps/web/src/app/shell/app-header.tsx` — IconButton com `Badge` (contador, `aria-label="Notificações: N não lidas"`, esconde badge em 0) como RouterLink para `/notificacoes`.
- Modify `apps/web/src/app/app-shell.tsx` — `NotificationsProvider` envolvendo `AppHeader` E o conteúdo roteado (Sol: header e main dentro do contexto, não só o Outlet).
- Modify `apps/web/src/app/help-page.tsx` — seção curta explicando leitura, empresa de origem e próximo passo (Manual #95).

- [ ] Steps: escrever → testes web focados (vitest filter notifications + shell/manifest/paths/help) → commit `feat(web): add notifications inbox page, unread counter and mark-as-read flow`.

### Task 9 (UI #95): Docs + gate + PR #95 + reviews

- [ ] `docs/verification/issue-95-notification-inbox-ui-test-cases.md` (BDD × evidência × situação; loading/empty/error/retry; desktop/mobile/light/dark/teclado).
- [ ] `./scripts/verify-public.sh` EXIT:0 no HEAD final (serializado).
- [ ] Terra review da UI (diff Task 7-8) e Sol review do diff final completo (migration/grants/RLS/tenant/concorrência/contador/allowlist); endereçar achaos; registro em `/tmp/ni94-` + resumo no PR.
- [ ] Push + PR #95 (base no PR #94; re-alvejar ao main após merge) + comentário na issue #95.

## Self-review do plano (rev. 4 — pós 3ª rodada Sol)

- 3ª rodada: (3) contratos de resultado tipados `NotificationInboxResult(State, Page?)` / `NotificationUnreadCountResult(State, UnreadCount = 0)` / `NotificationMarkReadResult(State)` com `Unauthorized`, assinaturas de `ListAsync`/`CountUnreadAsync`/`MarkReadAsync` os declarando ✓ (Task 2); (4) prova de espera por backend PID de B (`pg_backend_pid()` + `pg_stat_activity` `pid = @backendPidB AND wait_event_type = 'Lock'`, padrão `AuditMigrationTests.cs:350`) ✓ (decisão 8, Task 0); constraint de ordem corrigida para Task 0 RED antes da Task 1 migration ✓.
- 2ª rodada permanece fechada: grant cross-owner via SET ROLE ✓ (Task 1); cursor dono único C# ✓ (decisão 5); provider com reset/guarda de sessão ✓ (Task 7); corpo vazio 401/429 ✓ (Task 0); estimativa UI ~16 arquivos ✓; gh auth = artefato do sandbox ✓.
- 1ª rodada permanece fechada: handoff presente ✓; membership por `current_actor_memberships()` ✓; DI ✓; CSRF/no-store ✓; decisões (a)-(g) ✓.
