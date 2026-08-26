using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260826120000_AddTelegramNotificationChannel")]
public partial class AddTelegramNotificationChannel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            ALTER TABLE notifications.notification_preferences
                DROP CONSTRAINT notification_preferences_channel_check,
                ADD CONSTRAINT notification_preferences_channel_check
                    CHECK (channel IN ('whatsapp', 'telegram'));
            ALTER TABLE notifications.notification_templates
                DROP CONSTRAINT notification_templates_channel_check,
                ADD CONSTRAINT notification_templates_channel_check
                    CHECK (channel IN ('in_app', 'whatsapp', 'telegram')),
                ADD COLUMN telegram_text text NULL,
                ADD CONSTRAINT ck_notification_templates_telegram_text CHECK (
                    telegram_text IS NULL
                    OR (channel = 'telegram'
                        AND is_approved
                        AND btrim(telegram_text, E' \t\n\013\f\r') <> ''
                        AND telegram_text = btrim(telegram_text, E' \t\n\013\f\r')
                        AND char_length(telegram_text) BETWEEN 1 AND 4096));
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_channel_check,
                ADD CONSTRAINT notification_intents_channel_check
                    CHECK (channel IN ('in_app', 'whatsapp', 'telegram'));

            ALTER TABLE notifications.notification_recipient_contacts
                ALTER COLUMN phone_number DROP NOT NULL,
                ADD COLUMN address text NULL,
                DROP CONSTRAINT notification_recipient_contacts_channel_check,
                DROP CONSTRAINT notification_recipient_contacts_phone_number_check,
                ADD CONSTRAINT notification_recipient_contacts_channel_check
                    CHECK (channel IN ('whatsapp', 'telegram')),
                ADD CONSTRAINT ck_notification_recipient_contacts_channel_address CHECK (
                    (channel = 'whatsapp'
                        AND phone_number IS NOT NULL
                        AND phone_number ~ '^\+[1-9][0-9]{6,14}$'
                        AND address IS NULL)
                    OR (channel = 'telegram'
                        AND phone_number IS NULL
                        AND address IS NOT NULL
                        AND address = btrim(address)
                        AND address ~ '^[1-9][0-9]{0,15}$'
                        AND (char_length(address) < 16
                            OR address <= '4503599627370495')));

            CREATE OR REPLACE FUNCTION notifications.enqueue_notification_intent(
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
                    OR p_channel IS NULL OR p_channel NOT IN ('in_app', 'whatsapp', 'telegram')
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
                    SELECT 1
                    FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_recipient_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_templates AS template
                    WHERE template.company_id = p_company_id
                        AND template.channel = p_channel
                        AND template.template_key = p_template_key
                        AND template.is_approved
                        AND (p_channel <> 'telegram'
                            OR (template.telegram_text IS NOT NULL
                                AND btrim(template.telegram_text, E' \t\n\013\f\r') <> ''
                                AND template.telegram_text = btrim(template.telegram_text, E' \t\n\013\f\r')
                                AND char_length(template.telegram_text) BETWEEN 1 AND 4096)))
                THEN
                    RAISE EXCEPTION 'notification template is not approved' USING ERRCODE = '22023';
                END IF;

                IF p_channel <> 'in_app' THEN
                    SELECT preference.opted_in
                        INTO v_opted_in
                    FROM notifications.notification_preferences AS preference
                    WHERE preference.company_id = p_company_id
                        AND preference.user_id = p_recipient_user_id
                        AND preference.channel = p_channel;
                    IF COALESCE(v_opted_in, false) IS DISTINCT FROM true THEN
                        RETURN QUERY
                            SELECT 'suppressed_channel_opt_out'::text AS status, NULL::uuid AS delivery_id;
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
                RETURNING notification_intents.id INTO v_delivery_id;

                IF v_delivery_id IS NULL THEN
                    SELECT existing_intent.id
                        INTO v_delivery_id
                    FROM notifications.notification_intents AS existing_intent
                    WHERE existing_intent.event_key = p_event_key
                        AND existing_intent.recipient_user_id = p_recipient_user_id
                        AND existing_intent.channel = p_channel
                        AND existing_intent.template_key = p_template_key;
                    RETURN QUERY
                        SELECT 'replay'::text AS status, v_delivery_id AS delivery_id;
                    RETURN;
                END IF;

                PERFORM audit.append_event(
                    v_actor_user_id, 'company', p_company_id, 'web', 'notification.intent.enqueue',
                    'notification_intent', v_delivery_id, 'success', v_correlation_id, NULL,
                    jsonb_build_object('channel', p_channel, 'event_key', p_event_key));
                RETURN QUERY
                    SELECT 'created'::text AS status, v_delivery_id AS delivery_id;
            END;
            $function$;
            ALTER FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                TO vinx_runtime;

            CREATE OR REPLACE FUNCTION notifications.set_channel_preference(
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
                IF p_channel IS NULL OR p_channel NOT IN ('whatsapp', 'telegram') OR p_opted_in IS NULL THEN
                    RAISE EXCEPTION 'notification preference parameters are invalid' USING ERRCODE = '22023';
                END IF;
                IF v_actor_user_id <> p_user_id THEN
                    RAISE EXCEPTION 'notification preference changes are self-service' USING ERRCODE = '42501';
                END IF;
                PERFORM set_config('app.company_id', p_company_id::text, true);
                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
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
            ALTER FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                TO vinx_runtime;

            REVOKE ALL ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text);
            CREATE FUNCTION notifications.set_recipient_contact(
                p_company_id uuid,
                p_user_id uuid,
                p_channel text,
                p_address text)
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
                IF p_channel IS NULL
                    OR p_channel NOT IN ('whatsapp', 'telegram')
                    OR p_address IS NULL
                    OR (p_channel = 'whatsapp' AND p_address !~ '^\+[1-9][0-9]{6,14}$')
                    OR (p_channel = 'telegram'
                        AND (p_address <> btrim(p_address)
                            OR p_address !~ '^[1-9][0-9]{0,15}$'
                            OR (char_length(p_address) = 16 AND p_address > '4503599627370495'))) THEN
                    RAISE EXCEPTION 'notification contact parameters are invalid' USING ERRCODE = '22023';
                END IF;
                IF v_actor_user_id <> p_user_id THEN
                    RAISE EXCEPTION 'notification preference changes are self-service' USING ERRCODE = '42501';
                END IF;
                PERFORM set_config('app.company_id', p_company_id::text, true);
                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;

                INSERT INTO notifications.notification_recipient_contacts AS contact (
                    company_id, user_id, channel, phone_number, address, updated_by_user_id)
                VALUES (
                    p_company_id, p_user_id, p_channel,
                    CASE WHEN p_channel = 'whatsapp' THEN p_address ELSE NULL END,
                    CASE WHEN p_channel = 'telegram' THEN p_address ELSE NULL END,
                    v_actor_user_id)
                ON CONFLICT (company_id, user_id, channel) DO UPDATE
                    SET phone_number = EXCLUDED.phone_number,
                        address = EXCLUDED.address,
                        is_validated = false,
                        updated_at = clock_timestamp(),
                        updated_by_user_id = EXCLUDED.updated_by_user_id
                    WHERE contact.phone_number IS DISTINCT FROM EXCLUDED.phone_number
                        OR contact.address IS DISTINCT FROM EXCLUDED.address;
                GET DIAGNOSTICS v_changed = ROW_COUNT;
                IF v_changed > 0 THEN
                    PERFORM audit.append_event(
                        v_actor_user_id, 'company', p_company_id, 'web', 'notification.contact.change',
                        'notification_recipient_contact', p_user_id, 'success', v_correlation_id, NULL,
                        jsonb_build_object('channel', p_channel));
                END IF;
            END;
            $function$;
            ALTER FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM notifications.notification_preferences AS preference
                    WHERE preference.channel = 'telegram')
                    OR EXISTS (
                        SELECT 1
                        FROM notifications.notification_templates AS template
                        WHERE template.channel = 'telegram')
                    OR EXISTS (
                        SELECT 1
                        FROM notifications.notification_intents AS intent
                        WHERE intent.channel = 'telegram')
                    OR EXISTS (
                        SELECT 1
                        FROM notifications.notification_recipient_contacts AS contact
                        WHERE contact.channel = 'telegram') THEN
                    RAISE EXCEPTION 'cannot downgrade Telegram notification channel while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            REVOKE ALL ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            CREATE OR REPLACE FUNCTION notifications.enqueue_notification_intent(
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

                PERFORM set_config('app.company_id', p_company_id::text, true);
                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_recipient_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_templates AS template
                    WHERE template.company_id = p_company_id
                        AND template.channel = p_channel
                        AND template.template_key = p_template_key
                        AND template.is_approved)
                THEN
                    RAISE EXCEPTION 'notification template is not approved' USING ERRCODE = '22023';
                END IF;
                IF p_channel = 'whatsapp' THEN
                    SELECT preference.opted_in
                        INTO v_opted_in
                    FROM notifications.notification_preferences AS preference
                    WHERE preference.company_id = p_company_id
                        AND preference.user_id = p_recipient_user_id
                        AND preference.channel = 'whatsapp';
                    IF COALESCE(v_opted_in, false) IS DISTINCT FROM true THEN
                        RETURN QUERY
                            SELECT 'suppressed_channel_opt_out'::text AS status, NULL::uuid AS delivery_id;
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
                RETURNING notification_intents.id INTO v_delivery_id;
                IF v_delivery_id IS NULL THEN
                    SELECT existing_intent.id
                        INTO v_delivery_id
                    FROM notifications.notification_intents AS existing_intent
                    WHERE existing_intent.event_key = p_event_key
                        AND existing_intent.recipient_user_id = p_recipient_user_id
                        AND existing_intent.channel = p_channel
                        AND existing_intent.template_key = p_template_key;
                    RETURN QUERY
                        SELECT 'replay'::text AS status, v_delivery_id AS delivery_id;
                    RETURN;
                END IF;
                PERFORM audit.append_event(
                    v_actor_user_id, 'company', p_company_id, 'web', 'notification.intent.enqueue',
                    'notification_intent', v_delivery_id, 'success', v_correlation_id, NULL,
                    jsonb_build_object('channel', p_channel, 'event_key', p_event_key));
                RETURN QUERY
                    SELECT 'created'::text AS status, v_delivery_id AS delivery_id;
            END;
            $function$;
            ALTER FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                OWNER TO vinx_notifications_owner;
            GRANT EXECUTE ON FUNCTION notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)
                TO vinx_runtime;

            CREATE OR REPLACE FUNCTION notifications.set_channel_preference(
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
                    SELECT 1
                    FROM foundation.memberships AS membership
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
            ALTER FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                OWNER TO vinx_notifications_owner;
            GRANT EXECUTE ON FUNCTION notifications.set_channel_preference(uuid, uuid, text, boolean)
                TO vinx_runtime;

            REVOKE ALL ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text);
            CREATE FUNCTION notifications.set_recipient_contact(
                p_company_id uuid,
                p_user_id uuid,
                p_channel text,
                p_phone_number text)
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
                IF p_channel IS DISTINCT FROM 'whatsapp'
                    OR p_phone_number IS NULL OR p_phone_number !~ '^\+[1-9][0-9]{6,14}$' THEN
                    RAISE EXCEPTION 'notification contact parameters are invalid' USING ERRCODE = '22023';
                END IF;
                IF v_actor_user_id <> p_user_id THEN
                    RAISE EXCEPTION 'notification preference changes are self-service' USING ERRCODE = '42501';
                END IF;
                PERFORM set_config('app.company_id', p_company_id::text, true);
                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;
                INSERT INTO notifications.notification_recipient_contacts AS contact (
                    company_id, user_id, channel, phone_number, updated_by_user_id)
                VALUES (p_company_id, p_user_id, p_channel, p_phone_number, v_actor_user_id)
                ON CONFLICT (company_id, user_id, channel) DO UPDATE
                    SET phone_number = EXCLUDED.phone_number,
                        is_validated = false,
                        updated_at = clock_timestamp(),
                        updated_by_user_id = EXCLUDED.updated_by_user_id
                    WHERE contact.phone_number IS DISTINCT FROM EXCLUDED.phone_number;
                GET DIAGNOSTICS v_changed = ROW_COUNT;
                IF v_changed > 0 THEN
                    PERFORM audit.append_event(
                        v_actor_user_id, 'company', p_company_id, 'web', 'notification.contact.change',
                        'notification_recipient_contact', p_user_id, 'success', v_correlation_id, NULL,
                        jsonb_build_object('channel', p_channel));
                END IF;
            END;
            $function$;
            ALTER FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                TO vinx_runtime;

            ALTER TABLE notifications.notification_recipient_contacts
                DROP CONSTRAINT ck_notification_recipient_contacts_channel_address,
                DROP CONSTRAINT notification_recipient_contacts_channel_check,
                DROP COLUMN address,
                ALTER COLUMN phone_number SET NOT NULL,
                ADD CONSTRAINT notification_recipient_contacts_channel_check
                    CHECK (channel = 'whatsapp'),
                ADD CONSTRAINT notification_recipient_contacts_phone_number_check
                    CHECK (phone_number ~ '^\+[1-9][0-9]{6,14}$');
            ALTER TABLE notifications.notification_templates
                DROP CONSTRAINT ck_notification_templates_telegram_text,
                DROP COLUMN telegram_text,
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
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
