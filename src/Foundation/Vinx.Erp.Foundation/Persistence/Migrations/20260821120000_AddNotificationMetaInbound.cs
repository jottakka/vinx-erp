using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260821120000_AddNotificationMetaInbound")]
public partial class AddNotificationMetaInbound : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE TABLE notifications.inbound_whatsapp_messages (
                id uuid PRIMARY KEY,
                message_id text NOT NULL CHECK (message_id = btrim(message_id) AND char_length(message_id) BETWEEN 1 AND 200),
                wa_id text NOT NULL CHECK (wa_id ~ '^[1-9][0-9]{6,14}$'),
                media_id text NOT NULL CHECK (media_id = btrim(media_id) AND char_length(media_id) BETWEEN 1 AND 200),
                caption text NULL CHECK (caption IS NULL OR (caption = btrim(caption) AND char_length(caption) BETWEEN 1 AND 1000)),
                status text NOT NULL CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
                reply_message_id text NULL CHECK (reply_message_id IS NULL OR (reply_message_id = btrim(reply_message_id) AND char_length(reply_message_id) BETWEEN 1 AND 200)),
                failure_reason text NULL CHECK (failure_reason IS NULL OR char_length(failure_reason) <= 100),
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                replied_at timestamp with time zone,
                CONSTRAINT uq_inbound_whatsapp_messages_message_id UNIQUE (message_id)
            );
            ALTER TABLE notifications.inbound_whatsapp_messages ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.inbound_whatsapp_messages FORCE ROW LEVEL SECURITY;
            CREATE POLICY inbound_whatsapp_messages_owner_select ON notifications.inbound_whatsapp_messages
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY inbound_whatsapp_messages_owner_insert ON notifications.inbound_whatsapp_messages
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);
            CREATE POLICY inbound_whatsapp_messages_owner_update ON notifications.inbound_whatsapp_messages
                FOR UPDATE TO vinx_notifications_owner USING (true) WITH CHECK (true);

            CREATE FUNCTION notifications.record_inbound_whatsapp_message(
                p_inbound_id uuid,
                p_message_id text,
                p_wa_id text,
                p_media_id text,
                p_caption text)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$'
                    OR p_media_id IS NULL OR btrim(p_media_id) = '' OR char_length(p_media_id) > 200
                    OR (p_caption IS NOT NULL AND (btrim(p_caption) = '' OR char_length(p_caption) > 1000)) THEN
                    RAISE EXCEPTION 'inbound whatsapp message parameters are invalid' USING ERRCODE = '22023';
                END IF;

                -- Sem auditoria: o webhook não tem ator usuário e a linha
                -- deduplicada é a evidência durável (mesmo racional do
                -- record_provider_event do W3).
                INSERT INTO notifications.inbound_whatsapp_messages (
                    id, message_id, wa_id, media_id, caption, status)
                VALUES (
                    p_inbound_id, p_message_id, p_wa_id, p_media_id, p_caption, 'pending')
                ON CONFLICT (message_id) DO NOTHING;
                RETURN CASE WHEN FOUND THEN 'created' ELSE 'replay' END;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)
                TO vinx_runtime;

            CREATE FUNCTION notifications.claim_inbound_whatsapp_message(
                p_inbound_id uuid)
            RETURNS TABLE (status text, wa_id text, media_id text, caption text, claimed boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_whatsapp_messages%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_row
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::text, NULL::text, NULL::text, false;
                    RETURN;
                END IF;

                IF v_row.status IN ('completed', 'failed') THEN
                    RETURN QUERY SELECT v_row.status, NULL::text, NULL::text, NULL::text, false;
                    RETURN;
                END IF;

                IF v_row.status = 'pending' THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET status = 'processing'
                        WHERE message.id = p_inbound_id;
                END IF;

                RETURN QUERY SELECT 'processing'::text, v_row.wa_id, v_row.media_id, v_row.caption, true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.complete_inbound_whatsapp_message(
                p_inbound_id uuid,
                p_reply_message_id text)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_reply_message_id IS NULL OR btrim(p_reply_message_id) = ''
                    OR char_length(p_reply_message_id) > 200 THEN
                    RAISE EXCEPTION 'inbound completion parameters are invalid' USING ERRCODE = '22023';
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'completed',
                        reply_message_id = p_reply_message_id,
                        replied_at = clock_timestamp()
                    WHERE message.id = p_inbound_id
                        AND message.status = 'processing';
                RETURN FOUND;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                TO vinx_worker;

            CREATE FUNCTION notifications.fail_inbound_whatsapp_message(
                p_inbound_id uuid,
                p_failure_reason text)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_failure_reason IS NULL
                    OR p_failure_reason NOT IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous') THEN
                    RAISE EXCEPTION 'inbound failure parameters are invalid' USING ERRCODE = '22023';
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'failed',
                        failure_reason = p_failure_reason,
                        replied_at = clock_timestamp()
                    WHERE message.id = p_inbound_id
                        AND message.status = 'processing';
                RETURN FOUND;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
                TO vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            DO $block$
            BEGIN
                IF EXISTS (SELECT 1 FROM notifications.inbound_whatsapp_messages) THEN
                    RAISE EXCEPTION 'cannot downgrade notification meta inbound while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text);
            DROP FUNCTION notifications.claim_inbound_whatsapp_message(uuid);
            DROP FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text);
            DROP FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text);
            DROP TABLE notifications.inbound_whatsapp_messages;
            RESET ROLE;
            """);
}
