using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260818120000_AddNotificationIntentOutbox")]
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
            GRANT USAGE ON SCHEMA audit TO vinx_notifications_owner;
            GRANT EXECUTE ON FUNCTION foundation.current_authorized_company_ids()
                TO vinx_notifications_owner;

            CREATE SCHEMA notifications;
            GRANT USAGE ON SCHEMA notifications TO vinx_runtime;
            GRANT USAGE, CREATE ON SCHEMA notifications TO vinx_notifications_owner;

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
                template_key text NOT NULL CHECK (template_key = btrim(template_key) AND char_length(template_key) BETWEEN 1 AND 100),
                is_approved boolean NOT NULL DEFAULT false,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT pk_notification_templates PRIMARY KEY (company_id, channel, template_key)
            );
            CREATE TABLE notifications.notification_intents (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                recipient_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                event_key text NOT NULL CHECK (event_key = btrim(event_key) AND char_length(event_key) BETWEEN 1 AND 200),
                channel text NOT NULL CHECK (channel IN ('in_app', 'whatsapp')),
                template_key text NOT NULL CHECK (template_key = btrim(template_key) AND char_length(template_key) BETWEEN 1 AND 100),
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
            CREATE POLICY notification_preferences_owner_insert ON notifications.notification_preferences
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
                IF p_company_id IS NULL
                    OR p_recipient_user_id IS NULL
                    OR p_channel IS NULL OR p_channel NOT IN ('in_app', 'whatsapp')
                    OR p_event_key IS NULL OR btrim(p_event_key) = '' OR char_length(btrim(p_event_key)) > 200
                        OR p_event_key <> btrim(p_event_key)
                    OR p_template_key IS NULL OR btrim(p_template_key) = '' OR char_length(btrim(p_template_key)) > 100
                        OR p_template_key <> btrim(p_template_key)
                    OR (p_deep_link_selector IS NOT NULL
                        AND (p_deep_link_selector NOT LIKE '/%' OR char_length(p_deep_link_selector) > 200))
                THEN
                    RAISE EXCEPTION 'notification intent parameters are invalid' USING ERRCODE = '22023';
                END IF;

                -- Contexto transacional de empresa para as policies RLS de leitura,
                -- no mesmo padrão das funções de provisionamento e diretório.
                PERFORM set_config('app.company_id', p_company_id::text, true);

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
                RETURNING id INTO v_delivery_id;

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
                PERFORM set_config('app.company_id', p_company_id::text, true);
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
            REVOKE EXECUTE ON FUNCTION foundation.current_authorized_company_ids()
                FROM vinx_notifications_owner;
            REVOKE USAGE ON SCHEMA audit FROM vinx_notifications_owner;
            REVOKE REFERENCES (id) ON foundation.users FROM vinx_notifications_owner;
            REVOKE REFERENCES (id) ON foundation.companies FROM vinx_notifications_owner;
            REVOKE USAGE ON SCHEMA foundation FROM vinx_notifications_owner;
            """);
}
