using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260825000000_AddNotificationEmailChannel")]
public partial class AddNotificationEmailChannel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            -- A fronteira estreita de e-mail lê o endereço do cadastro de
            -- identidade para montar o destino da tentativa em curso.
            GRANT SELECT (id, email) ON TABLE foundation.users TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            -- Templates: o canal e-mail exige assunto e corpo HTML aprovados; os
            -- campos são nulos fora do canal e-mail (CHECK fechado por chave).
            ALTER TABLE notifications.notification_templates
                DROP CONSTRAINT notification_templates_channel_check;
            ALTER TABLE notifications.notification_templates
                ADD CONSTRAINT notification_templates_channel_check
                    CHECK (channel IN ('in_app', 'whatsapp', 'email'));
            ALTER TABLE notifications.notification_templates
                ADD COLUMN email_subject text NULL,
                ADD COLUMN email_body_html text NULL;
            ALTER TABLE notifications.notification_templates
                ADD CONSTRAINT notification_templates_email_subject_check
                    CHECK (
                        email_subject IS NULL
                        OR (channel = 'email' AND is_approved
                            AND email_subject = btrim(email_subject)
                            AND char_length(email_subject) BETWEEN 1 AND 200)
                    ),
                ADD CONSTRAINT notification_templates_email_body_check
                    CHECK (
                        email_body_html IS NULL
                        OR (channel = 'email' AND is_approved
                            AND char_length(email_body_html) BETWEEN 1 AND 65536)
                    );

            -- Intents: permissão do canal e-mail.
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_channel_check;
            ALTER TABLE notifications.notification_intents
                ADD CONSTRAINT notification_intents_channel_check
                    CHECK (channel IN ('in_app', 'whatsapp', 'email'));

            -- Preferências: o e-mail é opt-in por destinatário, como o WhatsApp.
            ALTER TABLE notifications.notification_preferences
                DROP CONSTRAINT notification_preferences_channel_check;
            ALTER TABLE notifications.notification_preferences
                ADD CONSTRAINT notification_preferences_channel_check
                    CHECK (channel IN ('whatsapp', 'email'));

            -- Substitui a função de enfileiramento para aceitar e-mail com a
            -- mesma revalidação de opt-in do WhatsApp no momento da chamada.
            DROP FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text);
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
                    OR p_channel IS NULL OR p_channel NOT IN ('in_app', 'whatsapp', 'email')
                    OR p_event_key IS NULL OR btrim(p_event_key) = '' OR char_length(btrim(p_event_key)) > 200
                        OR p_event_key <> btrim(p_event_key)
                    OR p_template_key IS NULL OR btrim(p_template_key) = '' OR char_length(btrim(p_template_key)) > 100
                        OR p_template_key <> btrim(p_template_key)
                    OR (p_deep_link_selector IS NOT NULL
                        AND (p_deep_link_selector NOT LIKE '/%' OR char_length(p_deep_link_selector) > 200))
                THEN
                    RAISE EXCEPTION 'notification intent parameters are invalid' USING ERRCODE = '22023';
                END IF;

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

                IF p_channel IN ('whatsapp', 'email') THEN
                    SELECT preference.opted_in INTO v_opted_in
                    FROM notifications.notification_preferences AS preference
                    WHERE preference.company_id = p_company_id
                        AND preference.user_id = p_recipient_user_id
                        AND preference.channel = p_channel;
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

            -- Substitui a função de preferência para aceitar o canal e-mail
            -- (self-service, mesmo escopo de ator do WhatsApp).
            DROP FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean);
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
                IF p_channel NOT IN ('whatsapp', 'email') OR p_opted_in IS NULL THEN
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

            -- Fronteira estreita do canal e-mail: o provider lê somente o destino,
            -- assunto e corpo aprovados da tentativa em curso; o endereço vem do
            -- cadastro de identidade (foundation.users.email), sem tabela própria.
            CREATE FUNCTION notifications.get_notification_email_delivery_target(
                p_delivery_id uuid,
                p_attempt_id uuid)
            RETURNS TABLE (email text, subject text, body_html text, deep_link_selector text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_delivery_id IS NULL OR p_attempt_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                RETURN QUERY
                SELECT user_record.email, template.email_subject, template.email_body_html, intent.deep_link_selector
                FROM notifications.notification_intents AS intent
                JOIN foundation.users AS user_record
                    ON user_record.id = intent.recipient_user_id
                JOIN notifications.notification_templates AS template
                    ON template.company_id = intent.company_id
                    AND template.channel = intent.channel
                    AND template.template_key = intent.template_key
                WHERE intent.id = p_delivery_id
                    AND intent.status = 'provider_in_flight'
                    AND intent.provider_attempt_id = p_attempt_id
                    AND intent.channel = 'email'
                    AND template.is_approved
                    AND template.email_subject IS NOT NULL
                    AND template.email_body_html IS NOT NULL;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT NULL::text, NULL::text, NULL::text, NULL::text;
                END IF;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.get_notification_email_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.get_notification_email_delivery_target(uuid, uuid)
                TO vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    // Down simétrico ao Up: após a barreira fail-closed contra estado de e-mail,
    // restaura as funções anteriores (enqueue_notification_intent e
    // set_channel_preference) no corpo exato criado pelo Up de
    // AddNotificationIntentOutbox — mesma coreografia de grants do próprio Up
    // (GRANT CREATE → SET ROLE → REVOKE PUBLIC/vinx_migrator → GRANT EXECUTE
    // vinx_runtime → RESET ROLE → REVOKE CREATE) — para que downgrades profundos
    // encontrem o estado pós-outbox em vez de morrerem no DROP não idempotente do
    // Down da própria AddNotificationIntentOutbox (20260818120000).
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            RESET ROLE;
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM notifications.notification_intents WHERE channel = 'email'
                ) OR EXISTS (
                    SELECT 1 FROM notifications.notification_preferences WHERE channel = 'email'
                ) OR EXISTS (
                    SELECT 1 FROM notifications.notification_templates WHERE channel = 'email'
                ) THEN
                    RAISE EXCEPTION 'cannot downgrade notification email channel while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            REVOKE ALL ON FUNCTION notifications.get_notification_email_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.get_notification_email_delivery_target(uuid, uuid);

            DROP FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text);
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

            DROP FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean);
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

            ALTER TABLE notifications.notification_templates
                DROP CONSTRAINT notification_templates_email_subject_check,
                DROP CONSTRAINT notification_templates_email_body_check,
                DROP COLUMN email_subject,
                DROP COLUMN email_body_html,
                DROP CONSTRAINT notification_templates_channel_check,
                ADD CONSTRAINT notification_templates_channel_check
                    CHECK (channel IN ('in_app', 'whatsapp'));
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_channel_check,
                ADD CONSTRAINT notification_intents_channel_check
                    CHECK (channel IN ('in_app', 'whatsapp'));
            ALTER TABLE notifications.notification_preferences
                DROP CONSTRAINT notification_preferences_channel_check,
                ADD CONSTRAINT notification_preferences_channel_check
                    CHECK (channel IN ('whatsapp'));
            RESET ROLE;
            REVOKE SELECT (id, email) ON TABLE foundation.users FROM vinx_notifications_owner;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
