using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260823120000_AddInboundWhatsAppReplySendState")]
public partial class AddInboundWhatsAppReplySendState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD COLUMN reply_started_at timestamp with time zone NULL,
                DROP CONSTRAINT inbound_whatsapp_messages_status_check,
                ADD CONSTRAINT inbound_whatsapp_messages_status_check
                    CHECK (status IN ('pending', 'processing', 'reply_in_flight', 'completed', 'failed'));

            CREATE FUNCTION notifications.begin_inbound_whatsapp_reply(
                p_inbound_id uuid)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                SELECT message.status INTO v_status
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF NOT FOUND OR v_status <> 'processing' THEN
                    RETURN false;
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'reply_in_flight',
                        reply_started_at = clock_timestamp()
                    WHERE message.id = p_inbound_id;
                RETURN true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.begin_inbound_whatsapp_reply(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.begin_inbound_whatsapp_reply(uuid)
                TO vinx_worker;

            CREATE OR REPLACE FUNCTION notifications.claim_inbound_whatsapp_message(
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

                IF v_row.status IN ('reply_in_flight', 'completed', 'failed') THEN
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

            CREATE OR REPLACE FUNCTION notifications.complete_inbound_whatsapp_message(
                p_inbound_id uuid,
                p_reply_message_id text)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_whatsapp_messages%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_reply_message_id IS NULL OR btrim(p_reply_message_id) = ''
                    OR char_length(p_reply_message_id) > 200 THEN
                    RAISE EXCEPTION 'inbound completion parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_row
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF v_row.status = 'completed' THEN
                    RETURN v_row.reply_message_id = p_reply_message_id;
                END IF;

                IF v_row.status IS DISTINCT FROM 'reply_in_flight' THEN
                    RETURN false;
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'completed',
                        reply_message_id = p_reply_message_id,
                        replied_at = clock_timestamp()
                    WHERE message.id = p_inbound_id;
                RETURN true;
            END;
            $function$;

            CREATE OR REPLACE FUNCTION notifications.fail_inbound_whatsapp_message(
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
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing') THEN
                    RAISE EXCEPTION 'inbound failure parameters are invalid' USING ERRCODE = '22023';
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'failed',
                        failure_reason = p_failure_reason,
                        replied_at = clock_timestamp()
                    WHERE message.id = p_inbound_id
                        AND (
                            (message.status = 'processing' AND p_failure_reason IN (
                                'ai_unconfigured', 'media_unavailable', 'media_invalid',
                                'ocr_failed', 'ai_failed', 'document_rejected',
                                'document_scan_unavailable', 'document_storage_unavailable',
                                'capability_missing'))
                            OR (message.status = 'reply_in_flight' AND p_failure_reason IN (
                                'reply_rejected', 'reply_ambiguous'))
                        );
                RETURN FOUND;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
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
                IF EXISTS (
                    SELECT 1
                    FROM notifications.inbound_whatsapp_messages
                    WHERE status = 'reply_in_flight' OR reply_started_at IS NOT NULL
                ) THEN
                    RAISE EXCEPTION 'cannot downgrade inbound whatsapp reply state while reply state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.begin_inbound_whatsapp_reply(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.begin_inbound_whatsapp_reply(uuid);

            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT inbound_whatsapp_messages_status_check,
                DROP COLUMN reply_started_at,
                ADD CONSTRAINT inbound_whatsapp_messages_status_check
                    CHECK (status IN ('pending', 'processing', 'completed', 'failed'));

            CREATE OR REPLACE FUNCTION notifications.claim_inbound_whatsapp_message(
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

            CREATE OR REPLACE FUNCTION notifications.complete_inbound_whatsapp_message(
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

            CREATE OR REPLACE FUNCTION notifications.fail_inbound_whatsapp_message(
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
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing') THEN
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
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.complete_inbound_whatsapp_message(uuid, text)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.fail_inbound_whatsapp_message(uuid, text)
                TO vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
