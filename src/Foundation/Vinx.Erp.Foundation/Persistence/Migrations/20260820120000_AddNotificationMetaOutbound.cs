using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260820120000_AddNotificationMetaOutbound")]
public partial class AddNotificationMetaOutbound : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            ALTER TABLE notifications.notification_templates
                ADD COLUMN meta_template_name text NULL
                    CHECK (meta_template_name IS NULL
                        OR (channel = 'whatsapp' AND is_approved
                            AND meta_template_name = btrim(meta_template_name)
                            AND char_length(meta_template_name) BETWEEN 1 AND 100)),
                ADD COLUMN meta_language text NULL
                    CHECK (meta_language IS NULL OR meta_language ~ '^[a-z]{2}(_[A-Z]{2})?$');

            CREATE TABLE notifications.notification_recipient_contacts (
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                channel text NOT NULL CHECK (channel = 'whatsapp'),
                phone_number text NOT NULL CHECK (phone_number ~ '^\+[1-9][0-9]{6,14}$'),
                is_validated boolean NOT NULL DEFAULT false,
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                CONSTRAINT pk_notification_recipient_contacts PRIMARY KEY (company_id, user_id, channel)
            );
            ALTER TABLE notifications.notification_recipient_contacts ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_recipient_contacts FORCE ROW LEVEL SECURITY;
            CREATE POLICY notification_recipient_contacts_owner_select ON notifications.notification_recipient_contacts
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY notification_recipient_contacts_owner_insert ON notifications.notification_recipient_contacts
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);
            CREATE POLICY notification_recipient_contacts_owner_update ON notifications.notification_recipient_contacts
                FOR UPDATE TO vinx_notifications_owner USING (true) WITH CHECK (true);

            CREATE TABLE notifications.notification_provider_events (
                id uuid PRIMARY KEY,
                message_id text NOT NULL CHECK (message_id = btrim(message_id) AND char_length(message_id) BETWEEN 1 AND 200),
                event_type text NOT NULL CHECK (event_type IN ('sent', 'delivered', 'read', 'failed')),
                status text NULL CHECK (status IS NULL OR char_length(status) <= 50),
                received_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_notification_provider_events UNIQUE (message_id, event_type)
            );
            ALTER TABLE notifications.notification_provider_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.notification_provider_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY notification_provider_events_owner_select ON notifications.notification_provider_events
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY notification_provider_events_owner_insert ON notifications.notification_provider_events
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);

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
                    SELECT 1 FROM foundation.memberships AS membership
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
            REVOKE ALL ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                TO vinx_runtime;

            CREATE FUNCTION notifications.get_notification_delivery_target(
                p_delivery_id uuid,
                p_attempt_id uuid)
            RETURNS TABLE (phone_number text, meta_template_name text, meta_language text, deep_link_selector text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_delivery_id IS NULL OR p_attempt_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                RETURN QUERY
                SELECT contact.phone_number, template.meta_template_name, template.meta_language, intent.deep_link_selector
                FROM notifications.notification_intents AS intent
                JOIN notifications.notification_recipient_contacts AS contact
                    ON contact.company_id = intent.company_id
                    AND contact.user_id = intent.recipient_user_id
                    AND contact.channel = intent.channel
                JOIN notifications.notification_templates AS template
                    ON template.company_id = intent.company_id
                    AND template.channel = intent.channel
                    AND template.template_key = intent.template_key
                WHERE intent.id = p_delivery_id
                    AND intent.status = 'provider_in_flight'
                    AND intent.provider_attempt_id = p_attempt_id
                    AND template.is_approved
                    AND template.meta_template_name IS NOT NULL
                    AND template.meta_language IS NOT NULL;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT NULL::text, NULL::text, NULL::text, NULL::text;
                END IF;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.get_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.get_notification_delivery_target(uuid, uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.record_provider_event(
                p_message_id text,
                p_event_type text,
                p_status text)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_inserted boolean := false;
            BEGIN
                IF p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_event_type NOT IN ('sent', 'delivered', 'read', 'failed')
                    OR (p_status IS NOT NULL AND char_length(p_status) > 50) THEN
                    RAISE EXCEPTION 'notification provider event parameters are invalid' USING ERRCODE = '22023';
                END IF;

                -- Sem auditoria: o webhook não tem ator usuário e a própria linha
                -- deduplicada é a evidência durável do callback do provider.
                INSERT INTO notifications.notification_provider_events (id, message_id, event_type, status)
                VALUES (gen_random_uuid(), p_message_id, p_event_type, p_status)
                ON CONFLICT (message_id, event_type) DO NOTHING;
                v_inserted := FOUND;

                RETURN v_inserted;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.record_provider_event(text, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.record_provider_event(text, text, text)
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
                IF EXISTS (SELECT 1 FROM notifications.notification_recipient_contacts)
                    OR EXISTS (SELECT 1 FROM notifications.notification_provider_events) THEN
                    RAISE EXCEPTION 'cannot downgrade notification meta outbound while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.get_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.record_provider_event(text, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.set_recipient_contact(uuid, uuid, text, text);
            DROP FUNCTION notifications.get_notification_delivery_target(uuid, uuid);
            DROP FUNCTION notifications.record_provider_event(text, text, text);
            DROP TABLE notifications.notification_provider_events;
            DROP TABLE notifications.notification_recipient_contacts;
            ALTER TABLE notifications.notification_templates
                DROP COLUMN meta_template_name,
                DROP COLUMN meta_language;
            RESET ROLE;
            """);
}
