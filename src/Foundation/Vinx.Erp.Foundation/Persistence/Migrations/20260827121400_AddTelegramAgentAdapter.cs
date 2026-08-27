using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

public partial class AddTelegramAgentAdapter : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            ALTER TABLE notifications.notification_recipient_contacts
                ADD COLUMN agent_channel_identity_id uuid NULL;
            WITH identity_groups AS (
                SELECT contact.user_id, contact.channel, contact.address, gen_random_uuid() AS identity_id
                FROM notifications.notification_recipient_contacts AS contact
                WHERE contact.channel = 'telegram'
                GROUP BY contact.user_id, contact.channel, contact.address
            )
            UPDATE notifications.notification_recipient_contacts AS contact
            SET agent_channel_identity_id = identity.identity_id
            FROM identity_groups AS identity
            WHERE contact.user_id = identity.user_id
                AND contact.channel = identity.channel
                AND contact.address = identity.address;
            ALTER TABLE notifications.notification_recipient_contacts
                ADD CONSTRAINT ck_notification_recipient_contacts_agent_identity CHECK (
                    (channel = 'telegram') = (agent_channel_identity_id IS NOT NULL));

            CREATE FUNCTION notifications.assign_telegram_agent_identity()
            RETURNS trigger
            LANGUAGE plpgsql
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF NEW.channel <> 'telegram' THEN
                    NEW.agent_channel_identity_id := NULL;
                    RETURN NEW;
                END IF;
                IF TG_OP = 'UPDATE'
                    AND OLD.channel = 'telegram'
                    AND OLD.user_id = NEW.user_id
                    AND OLD.address IS NOT DISTINCT FROM NEW.address THEN
                    NEW.agent_channel_identity_id := OLD.agent_channel_identity_id;
                    RETURN NEW;
                END IF;
                SELECT contact.agent_channel_identity_id
                    INTO NEW.agent_channel_identity_id
                FROM notifications.notification_recipient_contacts AS contact
                WHERE contact.channel = 'telegram'
                    AND contact.user_id = NEW.user_id
                    AND contact.address = NEW.address
                    AND contact.agent_channel_identity_id IS NOT NULL
                LIMIT 1;
                NEW.agent_channel_identity_id := COALESCE(
                    NEW.agent_channel_identity_id,
                    gen_random_uuid());
                RETURN NEW;
            END;
            $function$;
            CREATE TRIGGER assign_telegram_agent_identity
                BEFORE INSERT OR UPDATE OF channel, user_id, address
                ON notifications.notification_recipient_contacts
                FOR EACH ROW EXECUTE FUNCTION notifications.assign_telegram_agent_identity();

            CREATE TABLE notifications.agent_channel_company_preferences (
                channel_identity_id uuid PRIMARY KEY,
                channel text NOT NULL CHECK (channel = 'telegram'),
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                selected_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                selected_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                revoked_at timestamp with time zone NULL,
                CONSTRAINT ck_agent_channel_company_preference_actor CHECK (
                    user_id = selected_by_user_id)
            );
            CREATE TABLE notifications.agent_channel_company_selection_requests (
                request_id uuid PRIMARY KEY,
                channel_identity_id uuid NOT NULL,
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
            );
            ALTER TABLE notifications.agent_channel_company_preferences ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.agent_channel_company_preferences FORCE ROW LEVEL SECURITY;
            ALTER TABLE notifications.agent_channel_company_selection_requests ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.agent_channel_company_selection_requests FORCE ROW LEVEL SECURITY;
            CREATE POLICY agent_channel_company_preferences_owner_all
                ON notifications.agent_channel_company_preferences
                FOR ALL TO vinx_notifications_owner USING (true) WITH CHECK (true);
            CREATE POLICY agent_channel_company_selection_requests_owner_all
                ON notifications.agent_channel_company_selection_requests
                FOR ALL TO vinx_notifications_owner USING (true) WITH CHECK (true);

            ALTER TABLE notifications.notification_intents
                ADD COLUMN telegram_agent_text text NULL,
                ADD COLUMN telegram_agent_identity_id uuid NULL,
                ADD CONSTRAINT ck_notification_intents_telegram_agent_text CHECK (
                    telegram_agent_text IS NULL
                    OR (channel = 'telegram'
                        AND telegram_agent_identity_id IS NOT NULL
                        AND telegram_agent_text = btrim(telegram_agent_text, E' \t\n\013\f\r')
                        AND btrim(telegram_agent_text, E' \t\n\013\f\r') <> ''
                        AND char_length(telegram_agent_text) BETWEEN 1 AND 800)),
                ADD CONSTRAINT ck_notification_intents_telegram_agent_identity CHECK (
                    (telegram_agent_text IS NULL) = (telegram_agent_identity_id IS NULL));

            ALTER TABLE notifications.inbound_telegram_messages
                DROP CONSTRAINT inbound_telegram_messages_text_body_check,
                DROP CONSTRAINT ck_inbound_telegram_messages_resolution_identity,
                ALTER COLUMN text_body DROP NOT NULL,
                ADD COLUMN message_type text NOT NULL DEFAULT 'text'
                    CHECK (message_type IN ('text', 'photo', 'document')),
                ADD COLUMN media_id text NULL,
                ADD COLUMN media_size bigint NULL,
                ADD COLUMN media_mime_type text NULL,
                ADD COLUMN channel_identity_id uuid NULL,
                ADD COLUMN agent_status text NOT NULL DEFAULT 'agent_pending'
                    CHECK (agent_status IN (
                        'agent_pending', 'agent_in_flight', 'agent_completed',
                        'agent_failed', 'agent_ambiguous')),
                ADD COLUMN agent_failure_reason text NULL CHECK (
                    agent_failure_reason IS NULL OR agent_failure_reason IN (
                        'capability_missing', 'media_unavailable', 'document_rejected',
                        'document_scan_unavailable', 'document_storage_unavailable',
                        'agent_failed', 'agent_ambiguous')),
                ADD COLUMN agent_file_id uuid NULL REFERENCES foundation.files(id) ON DELETE RESTRICT,
                ADD COLUMN agent_delivery_id uuid NULL REFERENCES notifications.notification_intents(id) ON DELETE RESTRICT,
                ADD COLUMN agent_started_at timestamp with time zone NULL,
                ADD COLUMN agent_completed_at timestamp with time zone NULL,
                ADD CONSTRAINT ck_inbound_telegram_messages_payload CHECK (
                    (message_type = 'text'
                        AND text_body IS NOT NULL
                        AND btrim(text_body, E' \t\n\013\f\r') <> ''
                        AND char_length(text_body) BETWEEN 1 AND 4096
                        AND media_id IS NULL AND media_size IS NULL AND media_mime_type IS NULL)
                    OR (message_type IN ('photo', 'document')
                        AND (text_body IS NULL OR (
                            btrim(text_body, E' \t\n\013\f\r') <> ''
                            AND char_length(text_body) BETWEEN 1 AND 4096))
                        AND media_id IS NOT NULL
                        AND media_id ~ '^[A-Za-z0-9_-]{1,512}$'
                        AND media_size BETWEEN 1 AND 26214400
                        AND media_mime_type IN ('application/pdf', 'image/jpeg', 'image/png')
                        AND (message_type <> 'photo' OR media_mime_type = 'image/jpeg'))),
                ADD CONSTRAINT ck_inbound_telegram_messages_resolution_identity CHECK (
                    (resolution_state = 'admitted'
                        AND resolved_user_id IS NOT NULL
                        AND resolved_company_id IS NOT NULL
                        AND resolved_membership_id IS NOT NULL
                        AND channel_identity_id IS NOT NULL)
                    OR (resolution_state = 'selection_required'
                        AND resolved_user_id IS NOT NULL
                        AND resolved_company_id IS NULL
                        AND resolved_membership_id IS NULL
                        AND channel_identity_id IS NOT NULL)
                    OR (resolution_state IN ('unbound', 'ambiguous', 'unverified', 'inactive')
                        AND resolved_user_id IS NULL
                        AND resolved_company_id IS NULL
                        AND resolved_membership_id IS NULL
                        AND channel_identity_id IS NULL)),
                ADD CONSTRAINT ck_inbound_telegram_messages_agent_state CHECK (
                    (agent_status = 'agent_pending'
                        AND agent_started_at IS NULL
                        AND agent_completed_at IS NULL
                        AND agent_failure_reason IS NULL
                        AND agent_file_id IS NULL
                        AND agent_delivery_id IS NULL)
                    OR (agent_status = 'agent_in_flight'
                        AND agent_started_at IS NOT NULL
                        AND agent_completed_at IS NULL
                        AND agent_failure_reason IS NULL
                        AND agent_delivery_id IS NULL)
                    OR (agent_status = 'agent_completed'
                        AND agent_started_at IS NOT NULL
                        AND agent_completed_at IS NOT NULL
                        AND agent_failure_reason IS NULL)
                    OR (agent_status IN ('agent_failed', 'agent_ambiguous')
                        AND agent_started_at IS NOT NULL
                        AND agent_completed_at IS NOT NULL
                        AND agent_failure_reason IS NOT NULL
                        AND agent_delivery_id IS NULL));
            ALTER TABLE notifications.inbound_telegram_messages
                ALTER COLUMN message_type DROP DEFAULT;
            ALTER TABLE notifications.inbound_telegram_messages
                DROP CONSTRAINT inbound_telegram_messages_resolution_state_check,
                ADD CONSTRAINT inbound_telegram_messages_resolution_state_check CHECK (
                    resolution_state IN (
                        'admitted', 'selection_required', 'unbound',
                        'ambiguous', 'unverified', 'inactive'));

            CREATE OR REPLACE FUNCTION notifications.resolve_inbound_telegram_actor(p_chat_id bigint)
            RETURNS TABLE (resolution_state text, user_id uuid, company_id uuid, membership_id uuid)
            LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_user_id uuid;
                v_identity_id uuid;
                v_company_id uuid;
                v_membership_id uuid;
                v_active_count integer;
            BEGIN
                IF p_chat_id IS NULL OR p_chat_id NOT BETWEEN 1 AND 4503599627370495 THEN
                    RAISE EXCEPTION 'inbound telegram chat_id is invalid' USING ERRCODE = '22023';
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text) THEN
                    RETURN QUERY SELECT 'unbound'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                        AND contact.is_validated) THEN
                    RETURN QUERY SELECT 'unverified'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                IF (SELECT count(DISTINCT contact.user_id)
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                        AND contact.is_validated AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL) = 0 THEN
                    RETURN QUERY SELECT 'inactive'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                IF (SELECT count(DISTINCT contact.user_id)
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                        AND contact.is_validated AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL) <> 1
                    OR (SELECT count(DISTINCT contact.agent_channel_identity_id)
                        FROM notifications.notification_recipient_contacts AS contact
                        WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                            AND contact.is_validated) <> 1 THEN
                    RETURN QUERY SELECT 'ambiguous'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                SELECT (array_agg(DISTINCT contact.user_id ORDER BY contact.user_id))[1],
                    (array_agg(DISTINCT contact.agent_channel_identity_id
                        ORDER BY contact.agent_channel_identity_id))[1]
                    INTO v_user_id, v_identity_id
                FROM notifications.notification_recipient_contacts AS contact
                WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                    AND contact.is_validated;

                SELECT preference.company_id, membership.id
                    INTO v_company_id, v_membership_id
                FROM notifications.agent_channel_company_preferences AS preference
                JOIN notifications.notification_recipient_contacts AS contact
                    ON contact.agent_channel_identity_id = preference.channel_identity_id
                    AND contact.user_id = preference.user_id
                    AND contact.company_id = preference.company_id
                    AND contact.channel = preference.channel
                    AND contact.address = p_chat_id::text
                    AND contact.is_validated
                JOIN foundation.memberships AS membership
                    ON membership.company_id = preference.company_id
                    AND membership.user_id = preference.user_id
                    AND membership.deleted_at IS NULL
                JOIN foundation.companies AS company
                    ON company.id = preference.company_id AND company.deleted_at IS NULL
                WHERE preference.channel_identity_id = v_identity_id
                    AND preference.user_id = v_user_id
                    AND preference.revoked_at IS NULL
                LIMIT 1;
                IF v_company_id IS NOT NULL THEN
                    RETURN QUERY SELECT 'admitted'::text, v_user_id, v_company_id, v_membership_id;
                    RETURN;
                END IF;

                SELECT count(DISTINCT membership.id),
                    (array_agg(DISTINCT membership.company_id ORDER BY membership.company_id))[1],
                    (array_agg(DISTINCT membership.id ORDER BY membership.id))[1]
                    INTO v_active_count, v_company_id, v_membership_id
                FROM notifications.notification_recipient_contacts AS contact
                JOIN foundation.memberships AS membership
                    ON membership.company_id = contact.company_id
                    AND membership.user_id = contact.user_id
                    AND membership.deleted_at IS NULL
                JOIN foundation.companies AS company
                    ON company.id = membership.company_id AND company.deleted_at IS NULL
                WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                    AND contact.is_validated AND contact.user_id = v_user_id
                    AND contact.agent_channel_identity_id = v_identity_id;
                IF v_active_count = 1 THEN
                    RETURN QUERY SELECT 'admitted'::text, v_user_id, v_company_id, v_membership_id;
                ELSE
                    RETURN QUERY SELECT 'selection_required'::text, v_user_id, NULL::uuid, NULL::uuid;
                END IF;
            END;
            $function$;

            CREATE FUNCTION notifications.admit_inbound_telegram_message(
                p_inbound_id uuid,
                p_update_id bigint,
                p_chat_id bigint,
                p_message_id bigint,
                p_message_type text,
                p_text_body text,
                p_media_id text,
                p_media_size bigint,
                p_media_mime_type text)
            RETURNS TABLE (status text, resolution_state text, delivery_id uuid)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, audit, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_telegram_messages%ROWTYPE;
                v_resolution text;
                v_user_id uuid;
                v_company_id uuid;
                v_membership_id uuid;
                v_identity_id uuid;
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF p_inbound_id IS NULL OR p_update_id IS NULL OR p_update_id <= 0
                    OR p_chat_id IS NULL OR p_chat_id NOT BETWEEN 1 AND 4503599627370495
                    OR p_message_id IS NULL OR p_message_id <= 0
                    OR p_message_type NOT IN ('text', 'photo', 'document')
                    OR (p_message_type = 'text' AND (
                        p_text_body IS NULL OR btrim(p_text_body, E' \t\n\013\f\r') = ''
                        OR char_length(p_text_body) NOT BETWEEN 1 AND 4096
                        OR p_media_id IS NOT NULL OR p_media_size IS NOT NULL OR p_media_mime_type IS NOT NULL))
                    OR (p_message_type IN ('photo', 'document') AND (
                        (p_text_body IS NOT NULL AND (
                            btrim(p_text_body, E' \t\n\013\f\r') = ''
                            OR char_length(p_text_body) NOT BETWEEN 1 AND 4096))
                        OR p_media_id IS NULL OR p_media_id !~ '^[A-Za-z0-9_-]{1,512}$'
                        OR p_media_size NOT BETWEEN 1 AND 26214400
                        OR p_media_mime_type NOT IN ('application/pdf', 'image/jpeg', 'image/png')
                        OR (p_message_type = 'photo' AND p_media_mime_type <> 'image/jpeg'))) THEN
                    RAISE EXCEPTION 'inbound telegram admission parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT resolution.resolution_state, resolution.user_id,
                    resolution.company_id, resolution.membership_id
                    INTO v_resolution, v_user_id, v_company_id, v_membership_id
                FROM notifications.resolve_inbound_telegram_actor(p_chat_id) AS resolution;
                IF v_resolution IN ('admitted', 'selection_required') THEN
                    SELECT (array_agg(DISTINCT contact.agent_channel_identity_id
                        ORDER BY contact.agent_channel_identity_id))[1]
                        INTO v_identity_id
                    FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                        AND contact.is_validated AND contact.user_id = v_user_id;
                END IF;

                INSERT INTO notifications.inbound_telegram_messages (
                    id, update_id, chat_id, message_id, text_body, message_type,
                    media_id, media_size, media_mime_type, status, resolution_state,
                    resolved_user_id, resolved_company_id, resolved_membership_id,
                    channel_identity_id, resolved_at, created_at)
                VALUES (
                    p_inbound_id, p_update_id, p_chat_id, p_message_id, p_text_body, p_message_type,
                    p_media_id, p_media_size, p_media_mime_type, 'pending', v_resolution,
                    v_user_id, v_company_id, v_membership_id, v_identity_id, v_now, v_now)
                ON CONFLICT (chat_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN QUERY SELECT 'created'::text, v_resolution, NULL::uuid;
                    RETURN;
                END IF;

                SELECT message.* INTO v_existing
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.chat_id = p_chat_id AND message.message_id = p_message_id;
                IF v_existing.update_id = p_update_id
                    AND v_existing.message_type = p_message_type
                    AND v_existing.text_body IS NOT DISTINCT FROM p_text_body
                    AND v_existing.media_id IS NOT DISTINCT FROM p_media_id
                    AND v_existing.media_size IS NOT DISTINCT FROM p_media_size
                    AND v_existing.media_mime_type IS NOT DISTINCT FROM p_media_mime_type THEN
                    RETURN QUERY SELECT 'replay'::text, v_existing.resolution_state, NULL::uuid;
                ELSE
                    RETURN QUERY SELECT 'conflict'::text, v_existing.resolution_state, NULL::uuid;
                END IF;
            END;
            $function$;

            CREATE FUNCTION notifications.enqueue_inbound_telegram_selection_reply(
                p_inbound_id uuid)
            RETURNS TABLE (delivery_id uuid, created boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, audit, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_telegram_messages%ROWTYPE;
                v_anchor_company_id uuid;
                v_delivery_id uuid;
                v_event_key text;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound telegram selection reply is invalid' USING ERRCODE = '22023';
                END IF;
                SELECT * INTO v_row
                FROM notifications.inbound_telegram_messages
                WHERE id = p_inbound_id FOR UPDATE;
                IF NOT FOUND OR v_row.status <> 'completed'
                    OR v_row.resolution_state <> 'selection_required'
                    OR v_row.resolved_user_id IS NULL OR v_row.channel_identity_id IS NULL THEN
                    RETURN QUERY SELECT NULL::uuid, false;
                    RETURN;
                END IF;
                SELECT (array_agg(DISTINCT membership.company_id
                    ORDER BY membership.company_id))[1] INTO v_anchor_company_id
                FROM notifications.notification_recipient_contacts AS contact
                JOIN foundation.memberships AS membership
                    ON membership.company_id = contact.company_id
                    AND membership.user_id = contact.user_id
                    AND membership.deleted_at IS NULL
                JOIN foundation.companies AS company
                    ON company.id = membership.company_id AND company.deleted_at IS NULL
                WHERE contact.agent_channel_identity_id = v_row.channel_identity_id
                    AND contact.user_id = v_row.resolved_user_id
                    AND contact.is_validated;
                IF v_anchor_company_id IS NULL THEN
                    RETURN QUERY SELECT NULL::uuid, false;
                    RETURN;
                END IF;
                v_event_key := 'telegram-agent:' || p_inbound_id::text || ':selection_required';
                INSERT INTO notifications.notification_intents (
                    id, company_id, recipient_user_id, event_key, channel,
                    template_key, deep_link_selector, telegram_agent_text,
                    telegram_agent_identity_id, status, correlation_id, created_by_user_id)
                VALUES (
                    gen_random_uuid(), v_anchor_company_id, v_row.resolved_user_id,
                    v_event_key, 'telegram', '_telegram_agent_selection_required',
                    '/agente?channelSelection=' || p_inbound_id::text,
                    'Escolha a empresa para continuar', v_row.channel_identity_id,
                    'pending_delivery', p_inbound_id, v_row.resolved_user_id)
                ON CONFLICT (event_key, recipient_user_id, channel, template_key) DO NOTHING
                RETURNING id INTO v_delivery_id;
                IF v_delivery_id IS NULL THEN
                    SELECT intent.id INTO v_delivery_id
                    FROM notifications.notification_intents AS intent
                    WHERE intent.event_key = v_event_key
                        AND intent.recipient_user_id = v_row.resolved_user_id
                        AND intent.channel = 'telegram'
                        AND intent.template_key = '_telegram_agent_selection_required';
                    RETURN QUERY SELECT v_delivery_id, false;
                    RETURN;
                END IF;
                PERFORM audit.append_event(
                    v_row.resolved_user_id, 'company', v_anchor_company_id, 'worker',
                    'agent.channel.selection_required', 'inbound_telegram_message',
                    p_inbound_id, 'success', p_inbound_id, NULL,
                    jsonb_build_object('channel', 'telegram'));
                RETURN QUERY SELECT v_delivery_id, true;
            END;
            $function$;

            CREATE OR REPLACE FUNCTION notifications.complete_inbound_telegram_text(
                p_inbound_id uuid)
            RETURNS TABLE (status text, transitioned boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
                v_resolution_state text;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound telegram text receipt id is required' USING ERRCODE = '22023';
                END IF;
                SELECT message.status, message.resolution_state
                    INTO v_status, v_resolution_state
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.id = p_inbound_id FOR UPDATE;
                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false;
                    RETURN;
                END IF;
                IF v_resolution_state NOT IN ('admitted', 'selection_required') THEN
                    RETURN QUERY SELECT 'not_admitted'::text, false;
                    RETURN;
                END IF;
                IF v_status <> 'pending' THEN
                    RETURN QUERY SELECT 'completed'::text, false;
                    RETURN;
                END IF;
                UPDATE notifications.inbound_telegram_messages AS message
                SET status = 'completed', completed_at = clock_timestamp()
                WHERE message.id = p_inbound_id AND message.status = 'pending'
                    AND message.resolution_state IN ('admitted', 'selection_required');
                RETURN QUERY SELECT 'completed'::text, true;
            END;
            $function$;
            COMMENT ON FUNCTION notifications.complete_inbound_telegram_text(uuid) IS
                'Registra somente o recibo técnico da fila Telegram; o estado do Agente permanece separado.';

            CREATE OR REPLACE FUNCTION notifications.admit_inbound_telegram_text(
                p_inbound_id uuid, p_update_id bigint, p_chat_id bigint,
                p_message_id bigint, p_text_body text)
            RETURNS TABLE (status text, resolution_state text)
            LANGUAGE sql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
                SELECT admission.status, admission.resolution_state
                FROM notifications.admit_inbound_telegram_message(
                    p_inbound_id, p_update_id, p_chat_id, p_message_id,
                    'text', p_text_body, NULL, NULL, NULL) AS admission;
            $function$;

            CREATE FUNCTION notifications.claim_inbound_telegram_agent(p_inbound_id uuid)
            RETURNS TABLE (
                status text, claimed boolean, message_type text, media_id text,
                media_mime_type text, text_body text, resolved_user_id uuid,
                resolved_company_id uuid, resolved_membership_id uuid,
                agent_file_id uuid)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_telegram_messages%ROWTYPE;
                v_requires_file boolean;
                v_authorized boolean;
                v_terminal_recovery boolean;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound telegram agent id is required' USING ERRCODE = '22023';
                END IF;
                SELECT * INTO v_row FROM notifications.inbound_telegram_messages AS message
                WHERE message.id = p_inbound_id FOR UPDATE;
                IF NOT FOUND OR v_row.resolution_state <> 'admitted' THEN
                    RETURN QUERY SELECT 'not_admitted'::text, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                v_requires_file := v_row.message_type IN ('photo', 'document');
                PERFORM set_config('app.user_id', v_row.resolved_user_id::text, true);
                PERFORM set_config('app.company_id', v_row.resolved_company_id::text, true);
                SELECT v_row.agent_status = 'agent_in_flight'
                    AND (NOT v_requires_file OR EXISTS (
                        SELECT 1
                        FROM foundation.files AS file
                        WHERE file.id = v_row.agent_file_id
                            AND file.company_id = v_row.resolved_company_id
                            AND file.uploader_membership_id = v_row.resolved_membership_id
                            AND file.processing_state = 'confirmed'
                            AND file.classification = 'agentic_attachment'
                            AND file.deleted_at IS NULL))
                    AND EXISTS (
                        SELECT 1
                        FROM foundation.agent_execution_ledger AS execution
                        WHERE execution.company_id = v_row.resolved_company_id
                            AND execution.actor_user_id = v_row.resolved_user_id
                            AND execution.channel = 'telegram'
                            AND execution.inbound_message_id = p_inbound_id
                            AND execution.request_id = p_inbound_id
                            AND execution.journey = CASE
                                WHEN v_row.message_type = 'document'
                                    THEN 'paguai_from_document_v1'
                                ELSE 'read_only_summarize_v1' END
                            AND execution.state IN ('completed', 'failed_closed', 'ambiguous'))
                INTO v_terminal_recovery;
                IF v_row.agent_status <> 'agent_pending' AND NOT v_terminal_recovery THEN
                    RETURN QUERY SELECT v_row.agent_status, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                SELECT EXISTS (
                    SELECT 1 FROM foundation.memberships AS membership
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE membership.id = v_row.resolved_membership_id
                        AND membership.company_id = v_row.resolved_company_id
                        AND membership.user_id = v_row.resolved_user_id
                        AND membership.deleted_at IS NULL AND company.deleted_at IS NULL)
                    AND EXISTS (
                        SELECT 1 FROM foundation.role_assignments AS assignment
                        JOIN foundation.roles AS role ON role.id = assignment.role_id
                        JOIN foundation.role_capabilities AS grant_row
                            ON grant_row.role_id = role.id AND grant_row.company_id = role.company_id
                        JOIN foundation.capabilities AS capability ON capability.id = grant_row.capability_id
                        WHERE assignment.company_id = v_row.resolved_company_id
                            AND assignment.membership_id = v_row.resolved_membership_id
                            AND assignment.deleted_at IS NULL AND role.deleted_at IS NULL
                            AND grant_row.deleted_at IS NULL AND grant_row.scope = 'company'
                            AND capability.name = 'ai.read')
                    AND (NOT v_requires_file OR (
                        SELECT count(DISTINCT capability.name) = CASE
                            WHEN v_row.message_type = 'document' THEN 3 ELSE 2 END
                        FROM foundation.role_assignments AS assignment
                        JOIN foundation.roles AS role ON role.id = assignment.role_id
                        JOIN foundation.role_capabilities AS grant_row
                            ON grant_row.role_id = role.id AND grant_row.company_id = role.company_id
                        JOIN foundation.capabilities AS capability ON capability.id = grant_row.capability_id
                        WHERE assignment.company_id = v_row.resolved_company_id
                            AND assignment.membership_id = v_row.resolved_membership_id
                            AND assignment.deleted_at IS NULL AND role.deleted_at IS NULL
                            AND grant_row.deleted_at IS NULL AND grant_row.scope = 'company'
                            AND (capability.name IN (
                                    'foundation.files.upload', 'foundation.files.read-ocr')
                                OR (v_row.message_type = 'document'
                                    AND capability.name = 'paguai.draft.create'))))
                    INTO v_authorized;
                IF NOT v_authorized THEN
                    UPDATE notifications.inbound_telegram_messages
                    SET agent_status = 'agent_failed', agent_started_at = clock_timestamp(),
                        agent_completed_at = clock_timestamp(),
                        agent_failure_reason = 'capability_missing'
                    WHERE id = p_inbound_id;
                    RETURN QUERY SELECT 'capability_missing'::text, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                IF v_terminal_recovery THEN
                    RETURN QUERY SELECT 'terminal_recovery'::text, true, v_row.message_type,
                        v_row.media_id, v_row.media_mime_type, v_row.text_body,
                        v_row.resolved_user_id, v_row.resolved_company_id,
                        v_row.resolved_membership_id, v_row.agent_file_id;
                    RETURN;
                END IF;
                UPDATE notifications.inbound_telegram_messages
                SET agent_status = 'agent_in_flight', agent_started_at = clock_timestamp()
                WHERE id = p_inbound_id;
                RETURN QUERY SELECT 'agent_in_flight'::text, true, v_row.message_type,
                    v_row.media_id, v_row.media_mime_type, v_row.text_body,
                    v_row.resolved_user_id, v_row.resolved_company_id,
                    v_row.resolved_membership_id, NULL::uuid;
            END;
            $function$;

            CREATE FUNCTION notifications.record_inbound_telegram_agent_file(
                p_inbound_id uuid, p_file_id uuid)
            RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_user_id uuid;
                v_company_id uuid;
            BEGIN
                IF p_inbound_id IS NULL OR p_file_id IS NULL THEN
                    RAISE EXCEPTION 'inbound telegram agent file is invalid' USING ERRCODE = '22023';
                END IF;
                SELECT message.resolved_user_id, message.resolved_company_id
                INTO v_user_id, v_company_id
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.id = p_inbound_id;
                IF NOT FOUND THEN
                    RETURN false;
                END IF;
                PERFORM set_config('app.user_id', v_user_id::text, true);
                PERFORM set_config('app.company_id', v_company_id::text, true);
                UPDATE notifications.inbound_telegram_messages AS message
                SET agent_file_id = p_file_id
                WHERE message.id = p_inbound_id
                    AND message.agent_status = 'agent_in_flight'
                    AND message.message_type IN ('photo', 'document')
                    AND message.agent_file_id IS NULL
                    AND EXISTS (
                        SELECT 1 FROM foundation.files AS file
                        WHERE file.id = p_file_id
                            AND file.company_id = message.resolved_company_id
                            AND file.uploader_membership_id = message.resolved_membership_id
                            AND file.processing_state = 'confirmed'
                            AND file.classification = 'agentic_attachment'
                            AND file.deleted_at IS NULL);
                RETURN FOUND;
            END;
            $function$;

            CREATE FUNCTION notifications.release_inbound_telegram_agent(p_inbound_id uuid)
            RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                UPDATE notifications.inbound_telegram_messages AS message
                SET agent_status = 'agent_pending', agent_started_at = NULL
                WHERE message.id = p_inbound_id
                    AND message.agent_status = 'agent_in_flight'
                    AND message.agent_file_id IS NULL
                    AND message.agent_delivery_id IS NULL
                    AND NOT EXISTS (
                        SELECT 1 FROM foundation.agent_execution_ledger AS execution
                        WHERE execution.inbound_message_id = p_inbound_id);
                RETURN FOUND;
            END;
            $function$;

            CREATE FUNCTION notifications.enqueue_inbound_telegram_agent_reply(
                p_inbound_id uuid, p_result_kind text, p_reply_text text,
                p_deep_link_selector text)
            RETURNS TABLE (delivery_id uuid, created boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_telegram_messages%ROWTYPE;
                v_delivery_id uuid;
                v_event_key text;
            BEGIN
                IF p_inbound_id IS NULL OR p_result_kind NOT IN ('result', 'paguai_review')
                    OR p_reply_text IS NULL OR p_reply_text <> btrim(p_reply_text, E' \t\n\013\f\r')
                    OR btrim(p_reply_text, E' \t\n\013\f\r') = ''
                    OR char_length(p_reply_text) NOT BETWEEN 1 AND 800
                    OR (p_result_kind = 'result' AND (
                        p_deep_link_selector IS NOT NULL
                        OR lower(p_reply_text) LIKE '%http://%'
                        OR lower(p_reply_text) LIKE '%https://%'
                        OR lower(p_reply_text) LIKE '%www.%'))
                    OR (p_result_kind = 'paguai_review' AND (
                        p_reply_text <> 'Documento pronto para revisão'
                        OR p_deep_link_selector !~ '^/paguai\?proposal=[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')) THEN
                    RAISE EXCEPTION 'inbound telegram agent reply is invalid' USING ERRCODE = '22023';
                END IF;
                SELECT * INTO v_row FROM notifications.inbound_telegram_messages
                WHERE id = p_inbound_id FOR UPDATE;
                IF NOT FOUND OR v_row.resolution_state <> 'admitted'
                    OR v_row.agent_status <> 'agent_in_flight' THEN
                    RAISE EXCEPTION 'inbound telegram agent reply is unavailable' USING ERRCODE = '23514';
                END IF;
                v_event_key := 'telegram-agent:' || p_inbound_id::text || ':' || p_result_kind;
                INSERT INTO notifications.notification_intents (
                    id, company_id, recipient_user_id, event_key, channel,
                    template_key, deep_link_selector, telegram_agent_text,
                    telegram_agent_identity_id,
                    status, correlation_id, created_by_user_id)
                VALUES (
                    gen_random_uuid(), v_row.resolved_company_id, v_row.resolved_user_id,
                    v_event_key, 'telegram', '_telegram_agent_' || p_result_kind,
                    p_deep_link_selector, p_reply_text, v_row.channel_identity_id, 'pending_delivery',
                    p_inbound_id, v_row.resolved_user_id)
                ON CONFLICT (event_key, recipient_user_id, channel, template_key) DO NOTHING
                RETURNING id INTO v_delivery_id;
                IF v_delivery_id IS NULL THEN
                    SELECT intent.id INTO v_delivery_id
                    FROM notifications.notification_intents AS intent
                    WHERE intent.event_key = v_event_key
                        AND intent.recipient_user_id = v_row.resolved_user_id
                        AND intent.channel = 'telegram'
                        AND intent.template_key = '_telegram_agent_' || p_result_kind;
                    UPDATE notifications.inbound_telegram_messages
                    SET agent_delivery_id = v_delivery_id,
                        agent_status = 'agent_completed',
                        agent_completed_at = clock_timestamp()
                    WHERE id = p_inbound_id;
                    RETURN QUERY SELECT v_delivery_id, false;
                    RETURN;
                END IF;
                UPDATE notifications.inbound_telegram_messages
                SET agent_delivery_id = v_delivery_id,
                    agent_status = 'agent_completed',
                    agent_completed_at = clock_timestamp()
                WHERE id = p_inbound_id;
                RETURN QUERY SELECT v_delivery_id, true;
            END;
            $function$;

            CREATE FUNCTION notifications.finalize_inbound_telegram_agent(
                p_inbound_id uuid, p_result text, p_reason text)
            RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
                v_target text;
            BEGIN
                IF p_inbound_id IS NULL OR p_result NOT IN ('completed', 'failed', 'ambiguous')
                    OR (p_result = 'completed') <> (p_reason IS NULL)
                    OR (p_reason IS NOT NULL AND p_reason NOT IN (
                        'capability_missing', 'media_unavailable', 'document_rejected',
                        'document_scan_unavailable', 'document_storage_unavailable',
                        'agent_failed', 'agent_ambiguous')) THEN
                    RAISE EXCEPTION 'inbound telegram agent finalization is invalid' USING ERRCODE = '22023';
                END IF;
                SELECT agent_status INTO v_status
                FROM notifications.inbound_telegram_messages
                WHERE id = p_inbound_id FOR UPDATE;
                IF NOT FOUND THEN RETURN false; END IF;
                v_target := 'agent_' || p_result;
                IF v_status <> 'agent_in_flight' THEN RETURN v_status = v_target; END IF;
                UPDATE notifications.inbound_telegram_messages
                SET agent_status = v_target, agent_failure_reason = p_reason,
                    agent_completed_at = clock_timestamp()
                WHERE id = p_inbound_id;
                RETURN true;
            END;
            $function$;

            CREATE FUNCTION notifications.get_inbound_telegram_agent_delivery(p_inbound_id uuid)
            RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
                SELECT message.agent_delivery_id
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.id = p_inbound_id
                    AND message.agent_status = 'agent_completed';
            $function$;

            CREATE FUNCTION notifications.select_agent_channel_company(
                p_inbound_id uuid, p_actor_user_id uuid, p_company_id uuid, p_request_id uuid)
            RETURNS text LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, audit, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_telegram_messages%ROWTYPE;
                v_existing notifications.agent_channel_company_selection_requests%ROWTYPE;
                v_membership_id uuid;
            BEGIN
                IF p_inbound_id IS NULL OR p_actor_user_id IS NULL
                    OR p_company_id IS NULL OR p_request_id IS NULL THEN
                    RAISE EXCEPTION 'agent channel selection is invalid' USING ERRCODE = '22023';
                END IF;
                SELECT * INTO v_row FROM notifications.inbound_telegram_messages
                WHERE id = p_inbound_id FOR UPDATE;
                IF NOT FOUND OR v_row.resolution_state <> 'selection_required'
                    OR v_row.resolved_user_id <> p_actor_user_id
                    OR v_row.channel_identity_id IS NULL THEN
                    RETURN 'selection_unavailable';
                END IF;
                SELECT membership.id INTO v_membership_id
                FROM foundation.memberships AS membership
                JOIN foundation.companies AS company
                    ON company.id = membership.company_id AND company.deleted_at IS NULL
                JOIN notifications.notification_recipient_contacts AS contact
                    ON contact.company_id = membership.company_id
                    AND contact.user_id = membership.user_id
                    AND contact.channel = 'telegram'
                    AND contact.is_validated
                    AND contact.agent_channel_identity_id = v_row.channel_identity_id
                WHERE membership.company_id = p_company_id
                    AND membership.user_id = p_actor_user_id
                    AND membership.deleted_at IS NULL
                LIMIT 1;
                IF v_membership_id IS NULL THEN RETURN 'selection_unavailable'; END IF;

                INSERT INTO notifications.agent_channel_company_selection_requests (
                    request_id, channel_identity_id, user_id, company_id)
                VALUES (p_request_id, v_row.channel_identity_id, p_actor_user_id, p_company_id)
                ON CONFLICT (request_id) DO NOTHING;
                IF NOT FOUND THEN
                    SELECT * INTO v_existing
                    FROM notifications.agent_channel_company_selection_requests
                    WHERE request_id = p_request_id;
                    RETURN CASE
                        WHEN v_existing.channel_identity_id = v_row.channel_identity_id
                            AND v_existing.user_id = p_actor_user_id
                            AND v_existing.company_id = p_company_id THEN 'replay'
                        ELSE 'idempotency_conflict' END;
                END IF;

                INSERT INTO notifications.agent_channel_company_preferences AS preference (
                    channel_identity_id, channel, user_id, company_id, selected_by_user_id)
                VALUES (
                    v_row.channel_identity_id, 'telegram', p_actor_user_id,
                    p_company_id, p_actor_user_id)
                ON CONFLICT (channel_identity_id) DO UPDATE
                SET company_id = EXCLUDED.company_id,
                    selected_at = clock_timestamp(),
                    selected_by_user_id = EXCLUDED.selected_by_user_id,
                    revoked_at = NULL;
                PERFORM audit.append_event(
                    p_actor_user_id, 'company', p_company_id, 'web',
                    'agent.channel.company.select', 'agent_channel_company_preference',
                    v_row.channel_identity_id, 'success', p_request_id, NULL,
                    jsonb_build_object('channel', 'telegram'));
                RETURN 'selected';
            END;
            $function$;

            REVOKE ALL ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid);
            CREATE FUNCTION notifications.get_telegram_notification_delivery_target(
                p_delivery_id uuid, p_attempt_id uuid)
            RETURNS TABLE (chat_id text, message_text text, deep_link_selector text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                RETURN QUERY
                SELECT contact.address,
                    COALESCE(intent.telegram_agent_text, template.telegram_text),
                    CASE WHEN intent.telegram_agent_text IS NOT NULL
                        THEN intent.deep_link_selector ELSE NULL END
                FROM notifications.notification_intents AS intent
                JOIN notifications.notification_recipient_contacts AS contact
                    ON contact.company_id = intent.company_id
                    AND contact.user_id = intent.recipient_user_id
                    AND contact.channel = intent.channel
                    AND (intent.telegram_agent_identity_id IS NULL
                        OR contact.agent_channel_identity_id = intent.telegram_agent_identity_id)
                LEFT JOIN notifications.notification_templates AS template
                    ON template.company_id = intent.company_id
                    AND template.channel = intent.channel
                    AND template.template_key = intent.template_key
                WHERE intent.id = p_delivery_id AND intent.channel = 'telegram'
                    AND intent.status = 'provider_in_flight'
                    AND intent.provider_attempt_id = p_attempt_id
                    AND contact.is_validated
                    AND (intent.telegram_agent_text IS NULL OR EXISTS (
                        SELECT 1
                        FROM foundation.memberships AS membership
                        JOIN foundation.companies AS company
                            ON company.id = membership.company_id
                            AND company.deleted_at IS NULL
                        WHERE membership.company_id = intent.company_id
                            AND membership.user_id = intent.recipient_user_id
                            AND membership.deleted_at IS NULL))
                    AND ((intent.telegram_agent_text IS NOT NULL)
                        OR (template.is_approved AND template.telegram_text IS NOT NULL));
                IF NOT FOUND THEN RETURN QUERY SELECT NULL::text, NULL::text, NULL::text; END IF;
            END;
            $function$;

            ALTER FUNCTION notifications.resolve_inbound_telegram_actor(bigint) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.admit_inbound_telegram_message(uuid, bigint, bigint, bigint, text, text, text, bigint, text) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.enqueue_inbound_telegram_selection_reply(uuid) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.claim_inbound_telegram_agent(uuid) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.record_inbound_telegram_agent_file(uuid, uuid) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.release_inbound_telegram_agent(uuid) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.enqueue_inbound_telegram_agent_reply(uuid, text, text, text) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.finalize_inbound_telegram_agent(uuid, text, text) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.get_inbound_telegram_agent_delivery(uuid) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.select_agent_channel_company(uuid, uuid, uuid, uuid) OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid) OWNER TO vinx_notifications_owner;

            REVOKE ALL ON FUNCTION notifications.admit_inbound_telegram_message(uuid, bigint, bigint, bigint, text, text, text, bigint, text)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.admit_inbound_telegram_message(uuid, bigint, bigint, bigint, text, text, text, bigint, text)
                TO vinx_runtime;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_telegram_agent(uuid),
                notifications.record_inbound_telegram_agent_file(uuid, uuid),
                notifications.release_inbound_telegram_agent(uuid),
                notifications.enqueue_inbound_telegram_selection_reply(uuid),
                notifications.enqueue_inbound_telegram_agent_reply(uuid, text, text, text),
                notifications.finalize_inbound_telegram_agent(uuid, text, text),
                notifications.get_inbound_telegram_agent_delivery(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_telegram_agent(uuid),
                notifications.record_inbound_telegram_agent_file(uuid, uuid),
                notifications.release_inbound_telegram_agent(uuid),
                notifications.enqueue_inbound_telegram_selection_reply(uuid),
                notifications.enqueue_inbound_telegram_agent_reply(uuid, text, text, text),
                notifications.finalize_inbound_telegram_agent(uuid, text, text),
                notifications.get_inbound_telegram_agent_delivery(uuid)
                TO vinx_worker;
            REVOKE ALL ON FUNCTION notifications.select_agent_channel_company(uuid, uuid, uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.select_agent_channel_company(uuid, uuid, uuid, uuid)
                TO vinx_runtime;
            REVOKE ALL ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                TO vinx_worker;

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
                IF EXISTS (SELECT 1 FROM notifications.agent_channel_company_preferences)
                    OR EXISTS (SELECT 1 FROM notifications.agent_channel_company_selection_requests)
                    OR EXISTS (SELECT 1 FROM notifications.notification_intents WHERE telegram_agent_text IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM notifications.inbound_telegram_messages
                        WHERE message_type <> 'text' OR agent_status <> 'agent_pending'
                            OR agent_file_id IS NOT NULL OR agent_delivery_id IS NOT NULL
                            OR resolution_state = 'selection_required') THEN
                    RAISE EXCEPTION 'cannot downgrade telegram agent adapter while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            REVOKE ALL ON FUNCTION notifications.claim_inbound_telegram_agent(uuid),
                notifications.record_inbound_telegram_agent_file(uuid, uuid),
                notifications.release_inbound_telegram_agent(uuid),
                notifications.enqueue_inbound_telegram_selection_reply(uuid),
                notifications.enqueue_inbound_telegram_agent_reply(uuid, text, text, text),
                notifications.finalize_inbound_telegram_agent(uuid, text, text),
                notifications.get_inbound_telegram_agent_delivery(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.select_agent_channel_company(uuid, uuid, uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_telegram_message(uuid, bigint, bigint, bigint, text, text, text, bigint, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.claim_inbound_telegram_agent(uuid);
            DROP FUNCTION notifications.record_inbound_telegram_agent_file(uuid, uuid);
            DROP FUNCTION notifications.release_inbound_telegram_agent(uuid);
            DROP FUNCTION notifications.enqueue_inbound_telegram_selection_reply(uuid);
            DROP FUNCTION notifications.enqueue_inbound_telegram_agent_reply(uuid, text, text, text);
            DROP FUNCTION notifications.finalize_inbound_telegram_agent(uuid, text, text);
            DROP FUNCTION notifications.get_inbound_telegram_agent_delivery(uuid);
            DROP FUNCTION notifications.select_agent_channel_company(uuid, uuid, uuid, uuid);
            DROP FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text);
            DROP FUNCTION notifications.admit_inbound_telegram_message(uuid, bigint, bigint, bigint, text, text, text, bigint, text);

            REVOKE ALL ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid);
            CREATE FUNCTION notifications.get_telegram_notification_delivery_target(
                p_delivery_id uuid, p_attempt_id uuid)
            RETURNS TABLE (chat_id text, message_text text)
            LANGUAGE sql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
                SELECT contact.address, template.telegram_text
                FROM notifications.notification_intents AS intent
                JOIN notifications.notification_recipient_contacts AS contact
                    ON contact.company_id = intent.company_id
                    AND contact.user_id = intent.recipient_user_id
                    AND contact.channel = intent.channel
                JOIN notifications.notification_templates AS template
                    ON template.company_id = intent.company_id
                    AND template.channel = intent.channel
                    AND template.template_key = intent.template_key
                WHERE intent.id = p_delivery_id AND intent.channel = 'telegram'
                    AND intent.status = 'provider_in_flight'
                    AND intent.provider_attempt_id = p_attempt_id
                    AND contact.is_validated AND template.is_approved
                    AND template.telegram_text IS NOT NULL;
            $function$;
            ALTER FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                OWNER TO vinx_notifications_owner;
            GRANT EXECUTE ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                TO vinx_worker;

            ALTER TABLE notifications.inbound_telegram_messages
                DROP CONSTRAINT inbound_telegram_messages_resolution_state_check,
                DROP CONSTRAINT ck_inbound_telegram_messages_agent_state,
                DROP CONSTRAINT ck_inbound_telegram_messages_resolution_identity,
                DROP CONSTRAINT ck_inbound_telegram_messages_payload,
                DROP COLUMN agent_completed_at,
                DROP COLUMN agent_started_at,
                DROP COLUMN agent_delivery_id,
                DROP COLUMN agent_file_id,
                DROP COLUMN agent_failure_reason,
                DROP COLUMN agent_status,
                DROP COLUMN channel_identity_id,
                DROP COLUMN media_mime_type,
                DROP COLUMN media_size,
                DROP COLUMN media_id,
                DROP COLUMN message_type,
                ALTER COLUMN text_body SET NOT NULL,
                ADD CONSTRAINT inbound_telegram_messages_text_body_check CHECK (
                    btrim(text_body, E' \t\n\013\f\r') <> ''
                    AND char_length(text_body) BETWEEN 1 AND 4096),
                ADD CONSTRAINT inbound_telegram_messages_resolution_state_check CHECK (
                    resolution_state IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')),
                ADD CONSTRAINT ck_inbound_telegram_messages_resolution_identity CHECK (
                    (resolution_state = 'admitted'
                        AND resolved_user_id IS NOT NULL
                        AND resolved_company_id IS NOT NULL
                        AND resolved_membership_id IS NOT NULL)
                    OR (resolution_state IN ('unbound', 'ambiguous', 'unverified', 'inactive')
                        AND resolved_user_id IS NULL
                        AND resolved_company_id IS NULL
                        AND resolved_membership_id IS NULL));
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT ck_notification_intents_telegram_agent_text,
                DROP CONSTRAINT ck_notification_intents_telegram_agent_identity,
                DROP COLUMN telegram_agent_identity_id,
                DROP COLUMN telegram_agent_text;
            DROP TABLE notifications.agent_channel_company_selection_requests;
            DROP TABLE notifications.agent_channel_company_preferences;
            DROP TRIGGER assign_telegram_agent_identity ON notifications.notification_recipient_contacts;
            DROP FUNCTION notifications.assign_telegram_agent_identity();
            ALTER TABLE notifications.notification_recipient_contacts
                DROP CONSTRAINT ck_notification_recipient_contacts_agent_identity,
                DROP COLUMN agent_channel_identity_id;

            CREATE OR REPLACE FUNCTION notifications.resolve_inbound_telegram_actor(
                p_chat_id bigint)
            RETURNS TABLE (resolution_state text, user_id uuid, company_id uuid, membership_id uuid)
            LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_count integer;
            BEGIN
                IF p_chat_id IS NULL OR p_chat_id NOT BETWEEN 1 AND 4503599627370495 THEN
                    RAISE EXCEPTION 'inbound telegram chat_id is invalid' USING ERRCODE = '22023';
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text) THEN
                    RETURN QUERY SELECT 'unbound'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                        AND contact.is_validated) THEN
                    RETURN QUERY SELECT 'unverified'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                SELECT count(*) INTO v_count
                FROM (
                    SELECT DISTINCT membership.id
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company ON company.id = contact.company_id
                    WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                        AND contact.is_validated AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL) AS active;
                IF v_count = 0 THEN
                    RETURN QUERY SELECT 'inactive'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                IF v_count > 1 THEN
                    RETURN QUERY SELECT 'ambiguous'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;
                RETURN QUERY
                SELECT 'admitted'::text, membership.user_id, membership.company_id, membership.id
                FROM notifications.notification_recipient_contacts AS contact
                JOIN foundation.memberships AS membership
                    ON membership.company_id = contact.company_id
                    AND membership.user_id = contact.user_id
                JOIN foundation.companies AS company ON company.id = contact.company_id
                WHERE contact.channel = 'telegram' AND contact.address = p_chat_id::text
                    AND contact.is_validated AND membership.deleted_at IS NULL
                    AND company.deleted_at IS NULL
                LIMIT 1;
            END;
            $function$;

            CREATE FUNCTION notifications.admit_inbound_telegram_text(
                p_inbound_id uuid, p_update_id bigint, p_chat_id bigint,
                p_message_id bigint, p_text_body text)
            RETURNS TABLE (status text, resolution_state text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_telegram_messages%ROWTYPE;
                v_resolution text;
                v_user_id uuid;
                v_company_id uuid;
                v_membership_id uuid;
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF p_inbound_id IS NULL OR p_update_id IS NULL OR p_update_id <= 0
                    OR p_chat_id IS NULL OR p_chat_id NOT BETWEEN 1 AND 4503599627370495
                    OR p_message_id IS NULL OR p_message_id <= 0
                    OR p_text_body IS NULL OR btrim(p_text_body, E' \t\n\013\f\r') = ''
                    OR char_length(p_text_body) NOT BETWEEN 1 AND 4096 THEN
                    RAISE EXCEPTION 'inbound telegram text admission parameters are invalid' USING ERRCODE = '22023';
                END IF;
                SELECT resolution.resolution_state, resolution.user_id,
                    resolution.company_id, resolution.membership_id
                    INTO v_resolution, v_user_id, v_company_id, v_membership_id
                FROM notifications.resolve_inbound_telegram_actor(p_chat_id) AS resolution;
                INSERT INTO notifications.inbound_telegram_messages (
                    id, update_id, chat_id, message_id, text_body, status,
                    resolution_state, resolved_user_id, resolved_company_id,
                    resolved_membership_id, resolved_at, created_at)
                VALUES (
                    p_inbound_id, p_update_id, p_chat_id, p_message_id, p_text_body,
                    'pending', v_resolution, v_user_id, v_company_id,
                    v_membership_id, v_now, v_now)
                ON CONFLICT (chat_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN QUERY SELECT 'created'::text, v_resolution;
                    RETURN;
                END IF;
                SELECT message.* INTO v_existing
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.chat_id = p_chat_id AND message.message_id = p_message_id;
                IF v_existing.update_id = p_update_id AND v_existing.text_body = p_text_body THEN
                    RETURN QUERY SELECT 'replay'::text, v_existing.resolution_state;
                ELSE
                    RETURN QUERY SELECT 'conflict'::text, v_existing.resolution_state;
                END IF;
            END;
            $function$;
            CREATE OR REPLACE FUNCTION notifications.complete_inbound_telegram_text(
                p_inbound_id uuid)
            RETURNS TABLE (status text, transitioned boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
                v_resolution_state text;
            BEGIN
                SELECT message.status, message.resolution_state
                    INTO v_status, v_resolution_state
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.id = p_inbound_id FOR UPDATE;
                IF NOT FOUND THEN RETURN QUERY SELECT 'not_found'::text, false; RETURN; END IF;
                IF v_resolution_state <> 'admitted' THEN
                    RETURN QUERY SELECT 'not_admitted'::text, false; RETURN;
                END IF;
                IF v_status <> 'pending' THEN
                    RETURN QUERY SELECT 'completed'::text, false; RETURN;
                END IF;
                UPDATE notifications.inbound_telegram_messages AS message
                SET status = 'completed', completed_at = clock_timestamp()
                WHERE message.id = p_inbound_id AND message.status = 'pending'
                    AND message.resolution_state = 'admitted';
                RETURN QUERY SELECT 'completed'::text, true;
            END;
            $function$;
            ALTER FUNCTION notifications.resolve_inbound_telegram_actor(bigint)
                OWNER TO vinx_notifications_owner;
            ALTER FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text)
                TO vinx_runtime;

            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
