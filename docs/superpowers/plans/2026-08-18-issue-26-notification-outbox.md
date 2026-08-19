# [Notifications] Intenção, preferências e outbox transacional (#26) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** criar a intenção de notificação, preferências por canal e o outbox transacional Wolverine que o Worker (#48) e o adapter Meta (#28) consumirão, sem enviar WhatsApp e sem produtor real nesta fatia.

**Architecture:** a fatia segue o padrão canônico do convite de dono: função PostgreSQL SECURITY DEFINER valida eligibility + deduplica + audita na mesma transação do produtor; o serviço C# na Foundation enfileira os canais, publica `NotificationDeliveryRequested` (somente GUID opaco) via `IDbContextOutbox` antes do commit e faz flush tolerante a falha depois. Sem entidades EF novas (tabelas SQL brutas geridas por funções, como `foundation.pending_owner_invitations`); sem DDL Wolverine em startup (`AutoCreate.None` já garantido no host).

**Tech Stack:** .NET 10, EF Core 10 + Npgsql (somente transação/ADO bruto), WolverineFx.EntityFrameworkCore/Postgresql 6.28.2, Testcontainers.PostgreSql, xUnit.

## Global Constraints

- Branch `codex/issue-26-notification-outbox` a partir de `origin/main` (64b565a). Nunca escrever em `main`.
- Worker e Meta ficam FORA desta fatia: nada em `src/Hosts/Vinx.Erp.WorkerHost`, nenhum provider, nenhum webhook.
- `AutoCreate.None` inalterado nos hosts; storage Wolverine só via `db-apply` (deploy/testes).
- Mensagem com somente `Guid DeliveryId`; nunca PII, telefone, e-mail, corpo, payload bancário ou token em envelope, coluna, log, DLQ ou metadado.
- In-app é obrigatório: preferência de WhatsApp nunca bloqueia a intenção in-app.
- Opt-in de WhatsApp revalidado dentro da função no momento do enqueue (fonte: tabela de preferências).
- Status monotônico: W1 só cria `pending_delivery`; replay não cria segunda entrega nem altera status/tentativas.
- RLS: tabelas novas com ENABLE+FORCE ROW LEVEL SECURITY; `vinx_runtime` recebe somente USAGE no schema + EXECUTE nas funções; nenhum DML direto.
- Migração raw SQL com guard no Down; funções `SECURITY DEFINER SET search_path TO pg_catalog, notifications, pg_temp`; REVOKE FROM PUBLIC + vinx_migrator, GRANT EXECUTE TO vinx_runtime.
- Auditoria na mesma transação via `audit.append_event` (overload 11 args: actor, scope 'company', company, source 'web', action, resource, id, outcome, correlation, reason, metadata).
- Estimativa: 16 arquivos (3 edições de infra/config, 1 migração, 4 fonte novo, 5 testes novo, 3 docs) e ~1.250 linhas manuais — excede o alvo de 800 por coesão da fatia (uma única responsabilidade: intenção+outbox), justificativa registrada aqui e no PR, conforme AGENTS.md. Sem subsistemas independentes misturados: não há API, frontend, provider ou worker nesta fatia.
- Gate final: `./scripts/verify-public.sh` EXIT:0 no HEAD exato, SERIALIZADO (nenhum build/Docker pesado em paralelo).
- Logs: template `"{Operation} {Module} {Result} {Reason}"`, sem PII; Warning só em flush adiado; Error em indisponibilidade.

## Padrões canônicos a espelhar (ler antes de cada task)

- Produtor transacional: `src/Foundation/Vinx.Erp.Foundation/Platform/PlatformOrganizationProvisioningService.cs` (transação → `FoundationRlsContext.ApplyActorAsync` → ADO bruto → `outbox.Enroll`+`PublishAsync` → commit → `FlushOutgoingMessagesAsync` tolerante).
- Migração raw SQL + SECURITY DEFINER + RLS + grants: `Persistence/Migrations/20260816012000_AddPlatformOrganizationProvisioning.cs`.
- Mensagem opaca: `Platform/OwnerInvitationDeliveryRequested.cs`.
- Testes de outbox (leitura de fila, opacidade, rollback, concorrência): `tests/public/Vinx.Erp.Foundation.IntegrationTests/PlatformOrganizationProvisioningOutboxTests.cs`.
- Wolverine publish no ApiHost: `src/Hosts/Vinx.Erp.ApiHost/Program.cs` linhas 96-101.

---

### Task 1: Role de owner + migração `AddNotificationIntentOutbox`

**Files:**
- Modify: `infra/database/bootstrap-foundation-roles.sql` (adicionar `vinx_notifications_owner` NOLOGIN, espelhando `vinx_provisioning_owner`)
- Create: `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260818120000_AddNotificationIntentOutbox.cs` (sem `.Designer.cs`: migração raw SQL sem mudança de modelo, como `20260816020000_AddPaguaiDraftMetadata.cs`)

**Interfaces (para as tasks seguintes):**
- Função `notifications.enqueue_notification_intent(p_company_id uuid, p_recipient_user_id uuid, p_event_key text, p_channel text, p_template_key text, p_deep_link_selector text) RETURNS TABLE(status text, delivery_id uuid)` — status `created|replay|suppressed_channel_opt_out`; exceções `42501` (`notification actor is required`, `recipient is not a member of the company`) e `22023` (`notification intent parameters are invalid`, `notification template is not approved`).
- Função `notifications.set_channel_preference(p_company_id uuid, p_user_id uuid, p_channel text, p_opted_in boolean) RETURNS void` — exceções `42501` (`notification actor is required`, `notification preference changes are self-service`, `recipient is not a member of the company`) e `22023` (`notification preference parameters are invalid`).
- Tabelas: `notifications.notification_intents`, `notifications.notification_preferences`, `notifications.notification_templates` (DDL abaixo).

- [ ] **Step 1: Adicionar a role no bootstrap**

Em `infra/database/bootstrap-foundation-roles.sql`, na mesma seção dos owners no-login (ex.: junto de `vinx_provisioning_owner`), adicionar linha no estilo idempotente do arquivo:

```sql
CREATE ROLE vinx_notifications_owner NOLOGIN;
ALTER ROLE vinx_notifications_owner NOLOGIN;
```

(Manter a forma exata usada pelo arquivo: idempotente, sem senha, sem superuser. O fixture de teste embarca este arquivo, então a role existe nos testes automaticamente.)

- [ ] **Step 2: Escrever a migração completa**

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

public partial class AddNotificationIntentOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT USAGE ON SCHEMA foundation TO vinx_notifications_owner;
            GRANT REFERENCES (id) ON foundation.companies TO vinx_notifications_owner;
            GRANT REFERENCES (id) ON foundation.users TO vinx_notifications_owner;
            GRANT SELECT (company_id, user_id, deleted_at) ON foundation.memberships TO vinx_notifications_owner;
            SET ROLE vinx_audit_owner;
            GRANT EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                TO vinx_notifications_owner;
            RESET ROLE;

            CREATE SCHEMA notifications AUTHORIZATION vinx_notifications_owner;
            GRANT USAGE ON SCHEMA notifications TO vinx_runtime;

            SET ROLE vinx_notifications_owner;
            CREATE TABLE notifications.notification_preferences (
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                channel text NOT NULL CHECK (channel IN ('whatsapp')),
                opted_in boolean NOT NULL,
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                CONSTRAINT pk_notification_preferences PRIMARY KEY (company_id, user_id, channel)
            );
            CREATE TABLE notifications.notification_templates (
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                channel text NOT NULL CHECK (channel IN ('in_app', 'whatsapp')),
                template_key text NOT NULL CHECK (template_key <> btrim(template_key) IS NOT TRUE AND char_length(btrim(template_key)) BETWEEN 1 AND 100),
                is_approved boolean NOT NULL DEFAULT false,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT pk_notification_templates PRIMARY KEY (company_id, channel, template_key)
            );
            CREATE TABLE notifications.notification_intents (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                recipient_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                event_key text NOT NULL CHECK (event_key <> btrim(event_key) IS NOT TRUE AND char_length(btrim(event_key)) BETWEEN 1 AND 200),
                channel text NOT NULL CHECK (channel IN ('in_app', 'whatsapp')),
                template_key text NOT NULL CHECK (template_key <> btrim(template_key) IS NOT TRUE AND char_length(btrim(template_key)) BETWEEN 1 AND 100),
                deep_link_selector text CHECK (deep_link_selector IS NULL OR (deep_link_selector LIKE '/%' AND char_length(deep_link_selector) <= 200)),
                status text NOT NULL DEFAULT 'pending_delivery' CHECK (status IN ('pending_delivery')),
                attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                correlation_id uuid NOT NULL,
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_notification_intents_delivery UNIQUE (event_key, recipient_user_id, channel, template_key)
            );
            CREATE INDEX ix_notification_intents_company_recipient
                ON notifications.notification_intents (company_id, recipient_user_id, created_at);

            ALTER TABLE notifications.notification_preferences ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_preferences FORCE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_templates ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_templates FORCE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_intents ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_intents FORCE ROW LEVEL SECURITY;
            CREATE POLICY notification_preferences_owner_select ON notifications.notification_preferences
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY notification_preferences_owner_write ON notifications.notification_preferences
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);
            CREATE POLICY notification_preferences_owner_update ON notifications.notification_preferences
                FOR UPDATE TO vinx_notifications_owner USING (true) WITH CHECK (true);
            CREATE POLICY notification_templates_owner_select ON notifications.notification_templates
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY notification_intents_owner_select ON notifications.notification_intents
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY notification_intents_owner_insert ON notifications.notification_intents
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);
            CREATE POLICY notification_intents_owner_update ON notifications.notification_intents
                FOR UPDATE TO vinx_notifications_owner USING (true) WITH CHECK (true);

            CREATE FUNCTION notifications.enqueue_notification_intent(
                p_company_id uuid,
                p_recipient_user_id uuid,
                p_event_key text,
                p_channel text,
                p_template_key text,
                p_deep_link_selector text)
            RETURNS TABLE (status text, delivery_id uuid)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_correlation_id uuid := COALESCE(NULLIF(current_setting('app.correlation_id', true), '')::uuid, gen_random_uuid());
                v_delivery_id uuid;
                v_opted_in boolean;
            BEGIN
                IF v_actor_user_id IS NULL THEN
                    RAISE EXCEPTION 'notification actor is required' USING ERRCODE = '42501';
                END IF;
                IF p_company_id IS NULL OR p_recipient_user_id IS NULL
                    OR p_event_key IS NULL OR btrim(p_event_key) = '' OR char_length(btrim(p_event_key)) > 200
                        OR p_event_key <> btrim(p_event_key)
                    OR p_channel NOT IN ('in_app', 'whatsapp')
                    OR p_template_key IS NULL OR btrim(p_template_key) = '' OR char_length(btrim(p_template_key)) > 100
                        OR p_template_key <> btrim(p_template_key)
                    OR (p_deep_link_selector IS NOT NULL
                        AND (p_deep_link_selector NOT LIKE '/' || '%' OR char_length(p_deep_link_selector) > 200))
                THEN
                    RAISE EXCEPTION 'notification intent parameters are invalid' USING ERRCODE = '22023';
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_recipient_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM notifications.notification_templates AS template
                    WHERE template.company_id = p_company_id
                        AND template.channel = p_channel
                        AND template.template_key = p_template_key
                        AND template.is_approved)
                THEN
                    RAISE EXCEPTION 'notification template is not approved' USING ERRCODE = '22023';
                END IF;

                IF p_channel = 'whatsapp' THEN
                    SELECT preference.opted_in INTO v_opted_in
                    FROM notifications.notification_preferences AS preference
                    WHERE preference.company_id = p_company_id
                        AND preference.user_id = p_recipient_user_id
                        AND preference.channel = 'whatsapp';
                    IF COALESCE(v_opted_in, false) IS DISTINCT FROM true THEN
                        RETURN QUERY SELECT 'suppressed_channel_opt_out'::text, NULL::uuid;
                        RETURN;
                    END IF;
                END IF;

                INSERT INTO notifications.notification_intents (
                    id, company_id, recipient_user_id, event_key, channel, template_key,
                    deep_link_selector, status, correlation_id, created_by_user_id)
                VALUES (
                    gen_random_uuid(), p_company_id, p_recipient_user_id, p_event_key, p_channel, p_template_key,
                    p_deep_link_selector, 'pending_delivery', v_correlation_id, v_actor_user_id)
                ON CONFLICT (event_key, recipient_user_id, channel, template_key) DO NOTHING
                RETURNING intent.id INTO v_delivery_id
                -- nota: usar `RETURNING id INTO v_delivery_id`; o alias `intent` não existe no INSERT,
                -- escrever exatamente: RETURNING id INTO v_delivery_id;

                IF v_delivery_id IS NULL THEN
                    SELECT existing_intent.id INTO v_delivery_id
                    FROM notifications.notification_intents AS existing_intent
                    WHERE existing_intent.event_key = p_event_key
                        AND existing_intent.recipient_user_id = p_recipient_user_id
                        AND existing_intent.channel = p_channel
                        AND existing_intent.template_key = p_template_key;
                    RETURN QUERY SELECT 'replay'::text, v_delivery_id;
                    RETURN;
                END IF;

                PERFORM audit.append_event(
                    v_actor_user_id, 'company', p_company_id, 'web', 'notification.intent.enqueue',
                    'notification_intent', v_delivery_id, 'success', v_correlation_id, NULL,
                    jsonb_build_object('channel', p_channel, 'event_key', p_event_key));
                RETURN QUERY SELECT 'created'::text, v_delivery_id;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                TO vinx_runtime;

            CREATE FUNCTION notifications.set_channel_preference(
                p_company_id uuid,
                p_user_id uuid,
                p_channel text,
                p_opted_in boolean)
            RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_correlation_id uuid := COALESCE(NULLIF(current_setting('app.correlation_id', true), '')::uuid, gen_random_uuid());
                v_changed integer;
            BEGIN
                IF v_actor_user_id IS NULL THEN
                    RAISE EXCEPTION 'notification actor is required' USING ERRCODE = '42501';
                END IF;
                IF p_channel IS DISTINCT FROM 'whatsapp' OR p_opted_in IS NULL THEN
                    RAISE EXCEPTION 'notification preference parameters are invalid' USING ERRCODE = '22023';
                END IF;
                IF v_actor_user_id <> p_user_id THEN
                    RAISE EXCEPTION 'notification preference changes are self-service' USING ERRCODE = '42501';
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;

                INSERT INTO notifications.notification_preferences AS preference (
                    company_id, user_id, channel, opted_in, updated_by_user_id)
                VALUES (p_company_id, p_user_id, p_channel, p_opted_in, v_actor_user_id)
                ON CONFLICT (company_id, user_id, channel) DO UPDATE
                    SET opted_in = EXCLUDED.opted_in,
                        updated_at = clock_timestamp(),
                        updated_by_user_id = EXCLUDED.updated_by_user_id
                    WHERE preference.opted_in IS DISTINCT FROM EXCLUDED.opted_in;
                GET DIAGNOSTICS v_changed = ROW_COUNT;
                IF v_changed > 0 THEN
                    PERFORM audit.append_event(
                        v_actor_user_id, 'company', p_company_id, 'web', 'notification.preference.change',
                        'notification_preference', p_user_id, 'success', v_correlation_id, NULL,
                        jsonb_build_object('channel', p_channel, 'opted_in', p_opted_in));
                END IF;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            DO $block$
            BEGIN
                IF EXISTS (SELECT 1 FROM notifications.notification_intents)
                    OR EXISTS (SELECT 1 FROM notifications.notification_preferences)
                    OR EXISTS (SELECT 1 FROM notifications.notification_templates) THEN
                    RAISE EXCEPTION 'cannot downgrade notification intents while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text);
            DROP FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean);
            DROP TABLE notifications.notification_intents;
            DROP TABLE notifications.notification_preferences;
            DROP TABLE notifications.notification_templates;
            RESET ROLE;
            DROP SCHEMA notifications;
            SET ROLE vinx_audit_owner;
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_notifications_owner;
            RESET ROLE;
            REVOKE SELECT (company_id, user_id, deleted_at) ON foundation.memberships FROM vinx_notifications_owner;
            REVOKE REFERENCES (id) ON foundation.users FROM vinx_notifications_owner;
            REVOKE REFERENCES (id) ON foundation.companies FROM vinx_notifications_owner;
            REVOKE USAGE ON SCHEMA foundation FROM vinx_notifications_owner;
            """);
}
```

Cuidados ao transcrever: (1) no `RETURNING` do INSERT usar exatamente `RETURNING id INTO v_delivery_id;` (sem alias — remover o comentário do bloco acima); (2) o `#nullable disable` e o namespace idênticos aos das migrações irmãs; (3) conferir em `20260809184931_AddAuditScope.cs` + migrações posteriores qual é a assinatura VIVA de `audit.append_event` antes de escrever os grants — a referência canônica é o grant em `20260816012000` (`uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb`, 11 args).

- [ ] **Step 3: Build da solução**

Run: `dotnet build Vinx.Erp.sln --no-restore`
Expected: 0 errors (a migração compila sem Designer porque não altera o modelo EF).

- [ ] **Step 4: Commit**

```bash
git add infra/database/bootstrap-foundation-roles.sql \
  src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260818120000_AddNotificationIntentOutbox.cs
git commit -m "feat(notifications): add intent, preference and template schema with enqueuing functions"
```

---

### Task 2: Contratos + `NotificationIntentService` na Foundation

**Files:**
- Create: `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationChannel.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationDeliveryKey.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationDeliveryRequested.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationIntentService.cs` (comando, estados, outcomes e resultado no mesmo arquivo, como `PlatformOrganizationProvisioningService.cs`)

**Interfaces (para Tasks 3-4):**
- `NotificationIntentService.EnqueueAsync(ActorContext, NotificationIntentCommand, CancellationToken) → Task<NotificationEnqueueResult>`
- `NotificationIntentService.SetChannelPreferenceAsync(ActorContext, Guid companyId, string channel, bool optedIn, CancellationToken) → Task<NotificationPreferenceResult>`
- `NotificationIntentCommand(Guid CompanyId, Guid RecipientUserId, string EventKey, string InAppTemplateKey, string? WhatsAppTemplateKey = null, string? DeepLinkSelector = null)`
- `NotificationEnqueueResult(NotificationIntentEnqueueState State, NotificationIntentOutcome? WhatsApp = null, NotificationIntentOutcome? InApp = null)`; `NotificationIntentOutcome(string Channel, Guid? DeliveryId, string Status, bool Created, string? SuppressionReason = null)`
- `new NotificationDeliveryRequested(Guid DeliveryId)` — mensagem Wolverine.

- [ ] **Step 1: `NotificationChannel.cs`**

```csharp
namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Canais de notificação suportados; in-app é obrigatório e não é controlável por preferência.
/// </summary>
public static class NotificationChannel
{
    public const string InApp = "in_app";
    public const string WhatsApp = "whatsapp";
}
```

- [ ] **Step 2: `NotificationDeliveryKey.cs`**

```csharp
namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Chave de deduplicação de entrega: no máximo uma entrega por evento, destinatário, canal e template.
/// Espelha a constraint UNIQUE de notifications.notification_intents.
/// </summary>
public readonly record struct NotificationDeliveryKey(
    string EventKey,
    Guid RecipientUserId,
    string Channel,
    string TemplateKey
);
```

- [ ] **Step 3: `NotificationDeliveryRequested.cs`**

```csharp
namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Intenção de entrega enfileirada após commit; contém somente identificador opaco da entrega.
/// </summary>
public sealed record NotificationDeliveryRequested(Guid DeliveryId);
```

- [ ] **Step 4: `NotificationIntentService.cs`** (espelhar estrutura/estilo de `PlatformOrganizationProvisioningService.cs`, incluindo `AddParameter`, `TryNormalize`, mapeamento de `PostgresException` por `SqlState`+`MessageText`):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.Foundation.Notifications;

public sealed record NotificationIntentCommand(
    Guid CompanyId,
    Guid RecipientUserId,
    string EventKey,
    string InAppTemplateKey,
    string? WhatsAppTemplateKey = null,
    string? DeepLinkSelector = null
);

public enum NotificationIntentEnqueueState
{
    Enqueued,
    Invalid,
    Forbidden,
    Unavailable,
}

public sealed record NotificationIntentOutcome(
    string Channel,
    Guid? DeliveryId,
    string Status,
    bool Created,
    string? SuppressionReason = null
);

public sealed record NotificationEnqueueResult(
    NotificationIntentEnqueueState State,
    NotificationIntentOutcome? WhatsApp = null,
    NotificationIntentOutcome? InApp = null
);

public enum NotificationPreferenceState
{
    Updated,
    Invalid,
    Forbidden,
    Unavailable,
}

public sealed record NotificationPreferenceResult(NotificationPreferenceState State);

/// <summary>
/// Cria intenções de notificação e o evento Wolverine na mesma transação; o alerta
/// in-app é obrigatório e o opt-in de WhatsApp é revalidado dentro da função no enqueue.
/// </summary>
public sealed class NotificationIntentService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<NotificationIntentService> logger
)
{
    private const string Operation = "notification_intent";
    private const string Module = "foundation";

    public async Task<NotificationEnqueueResult> EnqueueAsync(
        ActorContext actor,
        NotificationIntentCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!TryNormalize(command, out var normalized))
        {
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Invalid);
        }

        var commitAttempted = false;
        NotificationIntentOutcome? whatsApp = null;
        NotificationIntentOutcome? inApp = null;
        var createdDeliveries = new List<Guid>();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            if (normalized.WhatsAppTemplateKey is not null)
            {
                whatsApp = await EnqueueChannelAsync(
                    transaction,
                    normalized,
                    NotificationChannel.WhatsApp,
                    normalized.WhatsAppTemplateKey,
                    createdDeliveries,
                    cancellationToken
                );
            }

            inApp = await EnqueueChannelAsync(
                transaction,
                normalized,
                NotificationChannel.InApp,
                normalized.InAppTemplateKey,
                createdDeliveries,
                cancellationToken
            );

            if (createdDeliveries.Count > 0)
            {
                outbox.Enroll(database);
                foreach (var deliveryId in createdDeliveries)
                {
                    await outbox.PublishAsync(
                        new NotificationDeliveryRequested(deliveryId),
                        new DeliveryOptions { CorrelationId = actor.CorrelationId.ToString("D") }
                    );
                }
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedAccessDenial(exception))
        {
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Forbidden);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedInvalidIntent(exception))
        {
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Invalid);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Notification intent enqueue {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                commitAttempted ? "outbox_commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Unavailable);
        }

        if (createdDeliveries.Count > 0)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Notification intent enqueue {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "enqueued",
                    "outbox_flush_deferred"
                );
            }
        }

        return new NotificationEnqueueResult(NotificationIntentEnqueueState.Enqueued, whatsApp, inApp);
    }

    public async Task<NotificationPreferenceResult> SetChannelPreferenceAsync(
        ActorContext actor,
        Guid companyId,
        string channel,
        bool optedIn,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || channel != NotificationChannel.WhatsApp)
        {
            return new NotificationPreferenceResult(NotificationPreferenceState.Invalid);
        }

        var commitAttempted = false;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var connection = database.Database.GetDbConnection();
            await using var sql = connection.CreateCommand();
            sql.Transaction = transaction.GetDbTransaction();
            sql.CommandText =
                "SELECT notifications.set_channel_preference(@company_id, @user_id, @channel, @opted_in)";
            AddParameter(sql, "company_id", companyId);
            AddParameter(sql, "user_id", actor.UserId);
            AddParameter(sql, "channel", channel);
            AddParameter(sql, "opted_in", optedIn);
            await sql.ExecuteNonQueryAsync(cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedAccessDenial(exception))
        {
            return new NotificationPreferenceResult(NotificationPreferenceState.Forbidden);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedInvalidIntent(exception))
        {
            return new NotificationPreferenceResult(NotificationPreferenceState.Invalid);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Notification preference change {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                commitAttempted ? "commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return new NotificationPreferenceResult(NotificationPreferenceState.Unavailable);
        }

        return new NotificationPreferenceResult(NotificationPreferenceState.Updated);
    }

    private async Task<NotificationIntentOutcome> EnqueueChannelAsync(
        IDbContextTransaction transaction,
        NormalizedCommand command,
        string channel,
        string templateKey,
        List<Guid> createdDeliveries,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText = """
            SELECT status, delivery_id
            FROM notifications.enqueue_notification_intent(
                @company_id, @recipient_user_id, @event_key, @channel, @template_key, @deep_link_selector)
            """;
        AddParameter(sql, "company_id", command.CompanyId);
        AddParameter(sql, "recipient_user_id", command.RecipientUserId);
        AddParameter(sql, "event_key", command.EventKey);
        AddParameter(sql, "channel", channel);
        AddParameter(sql, "template_key", templateKey);
        AddParameter(sql, "deep_link_selector", (object?)command.DeepLinkSelector ?? DBNull.Value);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Notification intent enqueuing did not return a result."
            );
        }

        var status = reader.GetString(0);
        var deliveryId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        if (status == "created")
        {
            createdDeliveries.Add(deliveryId!.Value);
            return new NotificationIntentOutcome(channel, deliveryId, "pending_delivery", true);
        }

        if (status == "replay")
        {
            return new NotificationIntentOutcome(channel, deliveryId, "pending_delivery", false);
        }

        if (status == "suppressed_channel_opt_out")
        {
            return new NotificationIntentOutcome(
                channel,
                null,
                "suppressed_channel_opt_out",
                false,
                "channel_opt_out"
            );
        }

        throw new InvalidOperationException(
            "Notification intent enqueuing returned an unknown state."
        );
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsExpectedAccessDenial(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
        && exception.MessageText
            is "notification actor is required"
                or "recipient is not a member of the company"
                or "notification preference changes are self-service";

    private static bool IsExpectedInvalidIntent(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InvalidParameterValue
        && exception.MessageText
            is "notification intent parameters are invalid"
                or "notification template is not approved"
                or "notification preference parameters are invalid";

    private static bool TryNormalize(
        NotificationIntentCommand command,
        out NormalizedCommand normalized
    )
    {
        normalized = default!;
        if (command.CompanyId == Guid.Empty || command.RecipientUserId == Guid.Empty)
        {
            return false;
        }

        var eventKey = command.EventKey?.Trim();
        var inAppTemplateKey = command.InAppTemplateKey?.Trim();
        var whatsAppTemplateKey = command.WhatsAppTemplateKey?.Trim();
        var deepLinkSelector = command.DeepLinkSelector?.Trim();
        if (
            string.IsNullOrWhiteSpace(eventKey)
            || eventKey.Length > 200
            || string.IsNullOrWhiteSpace(inAppTemplateKey)
            || inAppTemplateKey.Length > 100
            || (whatsAppTemplateKey is { Length: 0 })
            || whatsAppTemplateKey?.Length > 100
            || (deepLinkSelector is not null && (deepLinkSelector.Length > 200 || !deepLinkSelector.StartsWith('/')))
        )
        {
            return false;
        }

        normalized = new NormalizedCommand(
            command.CompanyId,
            command.RecipientUserId,
            eventKey,
            inAppTemplateKey,
            whatsAppTemplateKey is { Length: > 0 } ? whatsAppTemplateKey : null,
            deepLinkSelector is { Length: > 0 } ? deepLinkSelector : null
        );
        return true;
    }

    private sealed record NormalizedCommand(
        Guid CompanyId,
        Guid RecipientUserId,
        string EventKey,
        string InAppTemplateKey,
        string? WhatsAppTemplateKey,
        string? DeepLinkSelector
    );
}
```

Notas: `FoundationRlsContext` é `internal` na Foundation — o serviço está na mesma assembly. `using System.Data.Common;` é necessário para `DbCommand` (adicionar). `22023` = `PostgresErrorCodes.InvalidParameterValue` (conferir constante em Npgsql; provision usou `22023` literal em SQL e `CheckViolation`/`UniqueViolation` em C# — se `InvalidParameterValue` não existir, comparar `exception.SqlState == "22023"`).

- [ ] **Step 5: Build**

Run: `dotnet build src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj`
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Foundation/Vinx.Erp.Foundation/Notifications/
git commit -m "feat(notifications): add intent service, channel contracts and durable delivery message"
```

---

### Task 3: Wiring do ApiHost (publish + DI) e grants da fila nova

**Files:**
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs` (bloco Wolverine ~linhas 96-101 + registros AddScoped ~linhas 104-116 + using)
- Modify: `infra/database/grant-wolverine-runtime.sql` (DML do worker nas tabelas da fila nova — deploy correto já nesta fatia; o listener chega em #48)

- [ ] **Step 1: Program.cs — publish da fila nova**

Adicionar imediatamente após o bloco `PublishMessage<OwnerInvitationDeliveryRequested>()`:

```csharp
    options
        .PublishMessage<NotificationDeliveryRequested>()
        .ToPostgresqlQueue("notification-delivery")
        .UseDurableOutbox();
```

E `using Vinx.Erp.Foundation.Notifications;` junto aos usings existentes. Registrar o serviço junto aos demais:

```csharp
builder.Services.AddScoped<NotificationIntentService>();
```

- [ ] **Step 2: grant-wolverine-runtime.sql**

Ler o arquivo e adicionar as duas tabelas da fila nova à seção de grants DML do worker (mesma forma das linhas de `wolverine_queue_owner_invitation_delivery[_scheduled]`): `wolverine_queue_notification_delivery` e `wolverine_queue_notification_delivery_scheduled`, no schema `vinx_wolverine_queues`.

- [ ] **Step 3: Build + teste rápido de hosts**

Run: `dotnet build Vinx.Erp.sln --no-restore`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Hosts/Vinx.Erp.ApiHost/Program.cs infra/database/grant-wolverine-runtime.sql
git commit -m "feat(notifications): publish notification-delivery queue and register intent service"
```

---

### Task 4: Testes de integração (BDD 5 classes) — RED → GREEN

**Files:**
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/NotificationIntentSeed.cs` (helper de seeding + leitura de mensagens compartilhado)
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationIntentOutboxTests.cs` (Sucesso + Validação)
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationIntentStateTests.cs` (Estado: rollback, replay, opt-out, storage ausente)
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationIntentAccessTests.cs` (Acesso: cross-company, RLS direto, self-service)
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationIntentConcurrencyTests.cs` (Concorrência)

**Interfaces:**
- Consumes: `FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(aal)`, `factory.Fixture.OpenAdministratorConnectionAsync()`, `factory.Services`, `factory.ActorUserId`, `factory.CorrelationId`, `FoundationPostgresFixture.RuntimeConnectionString`; convenções de `PlatformOrganizationProvisioningOutboxTests` para leitura da fila e asserções de opacidade.

- [ ] **Step 1: Helper `NotificationIntentSeed.cs`**

```csharp
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

namespace Vinx.Erp.Foundation.IntegrationTests;

internal sealed record NotificationIntentSeed(
    Guid CompanyId,
    Guid RecipientUserId,
    string InAppTemplateKey,
    string WhatsAppTemplateKey
);

internal static class NotificationIntentSeed
{
    public static string NewEventKey() => $"event-{Guid.NewGuid():N}";

    public static async Task<NotificationIntentSeed> SeedCompanyWithRecipientAsync(
        FoundationApiFactory factory,
        bool whatsAppOptedIn = false,
        bool whatsAppTemplateApproved = true,
        bool inAppTemplateApproved = true
    )
    {
        var companyId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        const string inAppTemplateKey = "alert.in_app";
        const string whatsAppTemplateKey = "alert.whatsapp";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@id, @name, @actor, @actor, @request, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company, @id, @name, @actor, @actor, @request, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@recipient, @display, @email, clock_timestamp());
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company, @recipient, @actor, @actor, @request, clock_timestamp(), clock_timestamp());
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES (@company, 'in_app', @in_app_template, @in_app_approved);
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES (@company, 'whatsapp', @whatsapp_template, @whatsapp_approved);
            """,
            ("id", groupId), ... // parâmetros nomeados via NpgsqlParameter
        );
        if (whatsAppOptedIn)
        {
            await SetPreferenceAsync(factory, companyId, recipientId, optedIn: true);
        }
        return new NotificationIntentSeed(companyId, recipientId, inAppTemplateKey, whatsAppTemplateKey);
    }
    // SetPreferenceAsync: INSERT INTO notifications.notification_preferences ... ON CONFLICT DO UPDATE (seeding direto como admin)
    // CountIntentsAsync / ReadNotificationMessagesAsync / QueueExistsAsync: espelhar ReadOwnerInvitationMessagesAsync
    // apontando para vinx_wolverine_queues.wolverine_queue_notification_delivery.
}
```

(Transcrever com `NpgsqlParameter` reais; espelhar os helpers de `PlatformOrganizationProvisioningOutboxTests` para leitura da fila, opacidade e contagem de envelopes. `group_id` precisa ser gerado em C# para referência no INSERT da company.)

- [ ] **Step 2: RED — escrever os 4 arquivos de teste completos e rodar**

Testes (um método por cenário; todos criam `FoundationApiFactory.CreateWithAuthenticatedIdentityAsync()`, resolvem `NotificationIntentService` de `factory.Services.CreateAsyncScope()` e usam `new ActorContext(factory.ActorUserId, factory.CorrelationId)`):

`NotificationIntentOutboxTests` (Sucesso+Validação):
1. `Eligible_whatsapp_event_creates_both_intents_and_opaque_messages_after_commit` — seed opted-in; enqueue com WhatsApp+InApp; assert `State=Enqueued`, 2 outcomes `Created`, `Status="pending_delivery"`; fila `wolverine_queue_notification_delivery` com 2 mensagens cujo corpo desserializa em `NotificationDeliveryRequested` com os mesmos `DeliveryId`s e NÃO contém e-mail/display/evento/selector (opacidade, lista de strings proibidas como no teste canônico); 2 linhas `pending_delivery` com `attempt_count=0`; 2 auditorias `notification.intent.enqueue`.
2. `In_app_only_event_creates_single_intent_and_message` — sem `WhatsAppTemplateKey`; 1 outcome in-app, 1 mensagem, 1 linha.
3. `Opted_out_recipient_keeps_mandatory_in_app_alert_without_whatsapp_message` — seed sem opt-in; whatsapp outcome `Created=false`, `SuppressionReason="channel_opt_out"`, `DeliveryId=null`; in-app `Created=true`; 1 mensagem; 1 linha.
4. `Unapproved_template_rejects_enqueue_without_rows_or_messages` — seed `whatsappTemplateApproved:false`; `State=Invalid`; 0 linhas (nem in-app), 0 mensagens.
5. `Invalid_command_parameters_reject_without_side_effects` — `EventKey=""` (ou `DeepLinkSelector` sem `/`); `State=Invalid`; 0 linhas/mensagens.

`NotificationIntentStateTests`:
6. `Envelope_persistence_failure_before_commit_rolls_back_intents` — `REVOKE INSERT ON vinx_wolverine.wolverine_outgoing_envelopes FROM vinx_runtime` (admin); enqueue → `State=Unavailable`; 0 linhas, 0 mensagens, 0 auditorias de enqueue (restaurar grant no dispose não é necessário: factory por teste).
7. `Replayed_event_does_not_duplicate_deliveries_or_touch_status` — enqueue 2x mesmo comando; segunda: outcomes `Created=false` com mesmos `DeliveryId`s; fila permanece com 2 mensagens; linhas continuam `pending_delivery`, `attempt_count=0`, `updated_at` igual.
8. `Opt_out_after_first_enqueue_blocks_new_whatsapp_events_but_preserves_existing` — enqueue evento A (opted-in); `SetChannelPreferenceAsync` com actor=recipient (`new ActorContext(seed.RecipientUserId, Guid.NewGuid())`) optedIn=false → `Updated`; enqueue evento B → whatsapp suppressed + in-app created; linha do evento A permanece `pending_delivery` intocada.
9. `Enqueue_without_provisioned_storage_fails_closed_without_creating_storage` — `CreateWithoutWolverineStorageAsync`; enqueue → `State=Unavailable`; `to_regclass('vinx_wolverine_queues.wolverine_queue_notification_delivery')` IS NULL antes e depois; 0 linhas.

`NotificationIntentAccessTests`:
10. `Recipient_from_another_company_is_rejected_without_rows_or_messages` — seed empresa A; destinatário com membership somente na empresa B (segundo seed); enqueue em A → `State=Forbidden`; 0 linhas/mensagens.
11. `Runtime_role_cannot_access_intent_tables_directly` — conexão com `RuntimeConnectionString`: `SELECT/INSERT/UPDATE` em `notifications.notification_intents` → `42501`; `has_table_privilege('vinx_runtime', 'notifications.notification_intents', 'SELECT')` = false; `has_function_privilege('vinx_runtime', 'notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)', 'EXECUTE')` = true; idem `vinx_worker` sem privilégio algum no schema.
12. `Preference_change_for_another_user_is_denied` — actor A tenta `SetChannelPreferenceAsync` para recipient B → `Forbidden`; linha de preferência inalterada/não criada.

`NotificationIntentConcurrencyTests`:
13. `Concurrent_duplicate_events_produce_one_delivery_per_channel` — dois scopes independentes do mesmo factory; `Task.WhenAll` com stagger 25ms (padrão do teste canônico) com o MESMO comando; exatamente um `Created=true` por canal somando as duas respostas; fila com 2 mensagens; 2 linhas com ids distintos por canal.
14. `Distinct_events_for_same_recipient_create_separate_deliveries` — dois `EventKey`s → 4 outcomes `Created`, 4 linhas, 4 mensagens.

Run: `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~NotificationIntent"`
Expected inicial: FAIL (compile ou runtime) — RED. Depois de Tasks 1-3 completas, espera-se GREEN; qualquer falha é bug de implementação, corrigir na fonte (não afrouxar o teste).

- [ ] **Step 3: GREEN — rodar os 4 arquivos até passarem**

Run: `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~NotificationIntent"`
Expected: PASS em todos. Se flake de cold-start do container: reexecutar solo após ~1 min (protocolo de infra do handoff).

- [ ] **Step 4: Testes de regressão focados**

Run: `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~PlatformOrganizationProvisioning"` e `dotnet test tests/public/Vinx.Erp.ArchitectureTests`
Expected: PASS (nada quebrou no wiring Wolverine existente; arquitetura não ganhou entidades novas).

- [ ] **Step 5: Commit**

```bash
git add tests/public/Vinx.Erp.Foundation.IntegrationTests/
git commit -m "test(notifications): cover intent outbox success, validation, access, state and concurrency"
```

---

### Task 5: Documentação durável

**Files:**
- Modify: `docs/modules/foundation.md` (nova seção depois de "### Outbox transacional do convite inicial")
- Create: `docs/verification/issue-26-notification-outbox-test-cases.md`

- [ ] **Step 1: Seção em `docs/modules/foundation.md`** (português compacto; documentar tabelas, funções, dedup `(event_key, recipient_user_id, channel, template_key)`, in-app obrigatório, opt-in revalidado no enqueue, status `pending_delivery`, fila `notification-delivery`, sem PII, migração `20260818120000`). NÃO afirmar que Paguai gera alerta hoje.

- [ ] **Step 2: `docs/verification/issue-26-notification-outbox-test-cases.md`** — tabela com as 5 classes BDD × cenários × teste público × status (formato do `19-company-authorization-test-cases.md`), incluindo evidências de opacidade, RLS/grants, rollback e concorrência.

- [ ] **Step 3: Commit**

```bash
git add docs/modules/foundation.md docs/verification/issue-26-notification-outbox-test-cases.md
git commit -m "docs(notifications): document notification intent outbox and verification cases"
```

---

### Task 6: Gate completo serializado + PR

- [ ] **Step 1: Gate** — nenhum build/Docker em paralelo: `./scripts/verify-public.sh` → EXIT:0 no HEAD exato. Protocolo de flake: reexecutar solo após cooldown; evidenciar com teste isolado passando.
- [ ] **Step 2: Push + PR draft** — `git push -u origin codex/issue-26-notification-outbox`; `gh pr create --draft` com veredito, BDD, gate (EXIT:0 + HEAD), estimativa (>800 linhas justificada por coesão), invariantes preservados.
- [ ] **Step 3: Evidência** — comentário no PR com heads (base/head), suites rodadas e resultado; comentário na issue #26 vinculando o PR e o que ficou fora (worker #48, Meta #28, produtor real de evento de negócio).
- [ ] **Step 4: Ledger** — atualizar `HANDOFF-mvp-builder.md` (repo principal, fora do commit): marcar Trilha B item #26 parcial (outbox sem WhatsApp) no ledger da Seção 5.

## Self-review

- Cobertura do handoff W1: RED para pós-commit ✓ (testes 1, 6, 9), rollback sem outbox ✓ (6), destinatário elegível ✓ (1), opt-out ✓ (3, 8), template ausente ✓ (4), company mismatch ✓ (10); dedup por evento/destinatário/canal/template ✓ (7, 13); replay não duplica nem remove in-app ✓ (7); revalidação de opt-in no enqueue ✓ (função lê preferência no momento da chamada, testes 3/8); in-app obrigatório ✓ (3, 8); somente deep-link/valores permitidos ✓ (CHECK + opacidade no teste 1); evento Wolverine na transação do produtor ✓ (padrão provisioning, teste 6); storage técnico separado sem auto-create ✓ (9 + guards existentes); sem PII ✓ (opacidade 1, auditoria sem PII); Worker/Meta fora ✓ (nenhum arquivo deles tocado); verificação com PostgreSQL real, rollback, RLS/grants, concorrência ✓; `docs/verification` ✓ (Task 5); Paguai doc não alterado com evento fake ✓.
- Estimativa e limites: 16 arquivos (<25), ~1.250 linhas manuais (>800 com justificativa registrada aqui e no PR, permitido pelo AGENTS.md).
- Sem placeholders: código completo nas Tasks 1-3; Task 4 descreve cada método de teste com assertivos concretos e o helper esquematizado com SQL real — o executor transcreve seguindo o teste canônico nomeado.
