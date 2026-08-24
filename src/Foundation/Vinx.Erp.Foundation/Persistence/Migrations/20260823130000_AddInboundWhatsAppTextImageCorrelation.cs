using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260823130000_AddInboundWhatsAppTextImageCorrelation")]
public partial class AddInboundWhatsAppTextImageCorrelation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT inbound_whatsapp_messages_message_type_check,
                DROP CONSTRAINT inbound_whatsapp_messages_failure_reason_check,
                ALTER COLUMN media_id DROP NOT NULL,
                ADD COLUMN text_body text NULL,
                ADD COLUMN correlated_text_id uuid NULL,
                ADD COLUMN correlation_deadline timestamp with time zone NULL;

            UPDATE notifications.inbound_whatsapp_messages AS message
                SET correlation_deadline = message.created_at + interval '30 seconds'
            WHERE message.message_type = 'image'
                AND message.caption IS NULL;

            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD CONSTRAINT inbound_whatsapp_messages_message_type_check
                    CHECK (message_type IN ('image', 'document', 'text')),
                ADD CONSTRAINT inbound_whatsapp_messages_failure_reason_check
                    CHECK (failure_reason IS NULL OR failure_reason IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing',
                        'correlation_timeout', 'correlation_ambiguous')),
                ADD CONSTRAINT ck_inbound_whatsapp_messages_content
                    CHECK (
                        (message_type = 'text'
                            AND media_id IS NULL
                            AND caption IS NULL
                            AND text_body IS NOT NULL
                            AND btrim(text_body) <> ''
                            AND char_length(text_body) <= 4096)
                        OR (message_type IN ('image', 'document')
                            AND media_id IS NOT NULL
                            AND text_body IS NULL)),
                ADD CONSTRAINT ck_inbound_whatsapp_messages_correlation_deadline
                    CHECK (
                        ((message_type = 'text' OR (message_type = 'image' AND caption IS NULL))
                            AND correlation_deadline = created_at + interval '30 seconds')
                        OR ((message_type <> 'text' AND (message_type <> 'image' OR caption IS NOT NULL))
                            AND correlation_deadline IS NULL)),
                ADD CONSTRAINT ck_inbound_whatsapp_messages_correlated_text
                    CHECK (correlated_text_id IS NULL OR message_type = 'image'),
                ADD CONSTRAINT fk_inbound_whatsapp_messages_correlated_text
                    FOREIGN KEY (correlated_text_id)
                    REFERENCES notifications.inbound_whatsapp_messages(id)
                    ON DELETE RESTRICT,
                ADD CONSTRAINT uq_inbound_whatsapp_messages_correlated_text
                    UNIQUE (correlated_text_id);

            CREATE OR REPLACE FUNCTION notifications.admit_inbound_whatsapp_media(
                p_inbound_id uuid,
                p_phone_number_id text,
                p_message_id text,
                p_wa_id text,
                p_message_type text,
                p_media_id text,
                p_caption text,
                p_resolution_state text,
                p_user_id uuid,
                p_company_id uuid,
                p_membership_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_whatsapp_messages%ROWTYPE;
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_phone_number_id IS NULL OR btrim(p_phone_number_id) = '' OR char_length(p_phone_number_id) > 100
                    OR p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$'
                    OR p_media_id IS NULL OR btrim(p_media_id) = '' OR char_length(p_media_id) > 200
                    OR (p_caption IS NOT NULL AND (btrim(p_caption) = '' OR char_length(p_caption) > 1000))
                    OR p_message_type IS NULL OR p_message_type NOT IN ('image', 'document')
                    OR p_resolution_state IS NULL
                        OR p_resolution_state NOT IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')
                    OR (p_resolution_state = 'admitted') <> (
                        p_user_id IS NOT NULL
                        AND p_company_id IS NOT NULL
                        AND p_membership_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'inbound whatsapp media admission parameters are invalid' USING ERRCODE = '22023';
                END IF;

                INSERT INTO notifications.inbound_whatsapp_messages (
                    id, phone_number_id, message_id, wa_id, message_type, media_id, caption, status,
                    resolution_state, resolved_user_id, resolved_company_id, resolved_membership_id,
                    resolved_at, created_at, correlation_deadline)
                VALUES (
                    p_inbound_id, p_phone_number_id, p_message_id, p_wa_id, p_message_type, p_media_id, p_caption, 'pending',
                    p_resolution_state, p_user_id, p_company_id, p_membership_id,
                    v_now, v_now,
                    CASE WHEN p_message_type = 'image' AND p_caption IS NULL THEN v_now + interval '30 seconds' END)
                ON CONFLICT (phone_number_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN 'created';
                END IF;

                SELECT * INTO v_existing
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.phone_number_id = p_phone_number_id
                    AND message.message_id = p_message_id;

                IF v_existing.wa_id = p_wa_id
                    AND v_existing.message_type = p_message_type
                    AND v_existing.media_id = p_media_id THEN
                    RETURN 'replay';
                END IF;

                RETURN 'conflict';
            END;
            $function$;

            CREATE FUNCTION notifications.admit_inbound_whatsapp_text(
                p_inbound_id uuid,
                p_phone_number_id text,
                p_message_id text,
                p_wa_id text,
                p_text_body text,
                p_resolution_state text,
                p_user_id uuid,
                p_company_id uuid,
                p_membership_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_whatsapp_messages%ROWTYPE;
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_phone_number_id IS NULL OR btrim(p_phone_number_id) = '' OR char_length(p_phone_number_id) > 100
                    OR p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$'
                    OR p_text_body IS NULL OR btrim(p_text_body) = '' OR char_length(p_text_body) > 4096
                    OR p_resolution_state IS NULL
                        OR p_resolution_state NOT IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')
                    OR (p_resolution_state = 'admitted') <> (
                        p_user_id IS NOT NULL
                        AND p_company_id IS NOT NULL
                        AND p_membership_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'inbound whatsapp text admission parameters are invalid' USING ERRCODE = '22023';
                END IF;

                INSERT INTO notifications.inbound_whatsapp_messages (
                    id, phone_number_id, message_id, wa_id, message_type, text_body, status,
                    resolution_state, resolved_user_id, resolved_company_id, resolved_membership_id,
                    resolved_at, created_at, correlation_deadline)
                VALUES (
                    p_inbound_id, p_phone_number_id, p_message_id, p_wa_id, 'text', p_text_body, 'pending',
                    p_resolution_state, p_user_id, p_company_id, p_membership_id,
                    v_now, v_now, v_now + interval '30 seconds')
                ON CONFLICT (phone_number_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN 'created';
                END IF;

                SELECT * INTO v_existing
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.phone_number_id = p_phone_number_id
                    AND message.message_id = p_message_id;

                IF v_existing.wa_id = p_wa_id
                    AND v_existing.message_type = 'text'
                    AND v_existing.text_body = p_text_body THEN
                    RETURN 'replay';
                END IF;

                RETURN 'conflict';
            END;
            $function$;

            CREATE FUNCTION notifications.correlate_inbound_whatsapp_text_image(p_inbound_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_source notifications.inbound_whatsapp_messages%ROWTYPE;
                v_candidate_ids uuid[];
                v_candidate_id uuid;
                v_key text;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_source
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id;
                IF NOT FOUND THEN
                    RETURN 'not_found';
                END IF;

                IF v_source.resolution_state <> 'admitted'
                    OR v_source.status <> 'pending'
                    OR v_source.correlation_deadline IS NULL
                    OR v_source.correlation_deadline <= clock_timestamp()
                    OR (v_source.message_type = 'image' AND v_source.caption IS NOT NULL)
                    OR v_source.message_type NOT IN ('image', 'text') THEN
                    RETURN 'no_match';
                END IF;

                v_key := concat_ws('|', v_source.phone_number_id, v_source.wa_id,
                    v_source.resolved_user_id, v_source.resolved_company_id, v_source.resolved_membership_id);
                PERFORM pg_advisory_xact_lock(hashtextextended(v_key, 0));

                SELECT * INTO v_source
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF v_source.status <> 'pending'
                    OR v_source.correlation_deadline IS NULL
                    OR v_source.correlation_deadline <= clock_timestamp()
                    OR v_source.correlated_text_id IS NOT NULL
                    OR (v_source.message_type = 'text' AND EXISTS (
                        SELECT 1
                        FROM notifications.inbound_whatsapp_messages AS correlated_image
                        WHERE correlated_image.correlated_text_id = v_source.id)) THEN
                    RETURN 'no_match';
                END IF;

                SELECT array_agg(candidate.id ORDER BY candidate.id)
                INTO v_candidate_ids
                FROM (
                    SELECT message.id
                    FROM notifications.inbound_whatsapp_messages AS message
                    WHERE message.id <> v_source.id
                        AND message.status = 'pending'
                        AND message.resolution_state = 'admitted'
                        AND message.phone_number_id = v_source.phone_number_id
                        AND message.wa_id = v_source.wa_id
                        AND message.resolved_user_id = v_source.resolved_user_id
                        AND message.resolved_company_id = v_source.resolved_company_id
                        AND message.resolved_membership_id = v_source.resolved_membership_id
                        AND message.correlation_deadline > clock_timestamp()
                        AND abs(extract(epoch FROM message.created_at - v_source.created_at)) <= 30
                        AND ((v_source.message_type = 'text'
                                AND message.message_type = 'image'
                                AND message.caption IS NULL
                                AND message.correlated_text_id IS NULL)
                            OR (v_source.message_type = 'image'
                                AND message.message_type = 'text'
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM notifications.inbound_whatsapp_messages AS correlated_image
                                    WHERE correlated_image.correlated_text_id = message.id)))
                    ORDER BY message.id
                    FOR UPDATE
                ) AS candidate;

                IF coalesce(cardinality(v_candidate_ids), 0) = 0 THEN
                    RETURN 'no_match';
                END IF;

                IF cardinality(v_candidate_ids) > 1 THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET status = 'failed', failure_reason = 'correlation_ambiguous'
                    WHERE message.id = ANY(array_append(v_candidate_ids, v_source.id))
                        AND message.status = 'pending';
                    RETURN 'ambiguous';
                END IF;

                v_candidate_id := v_candidate_ids[1];
                IF v_source.message_type = 'image' THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET correlated_text_id = v_candidate_id
                    WHERE message.id = v_source.id AND message.correlated_text_id IS NULL;
                ELSE
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET correlated_text_id = v_source.id
                    WHERE message.id = v_candidate_id AND message.correlated_text_id IS NULL;
                END IF;
                RETURN CASE WHEN FOUND THEN 'correlated' ELSE 'no_match' END;
            END;
            $function$;

            CREATE FUNCTION notifications.expire_inbound_whatsapp_text(p_inbound_id uuid)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'failed', failure_reason = 'correlation_timeout'
                WHERE message.id = p_inbound_id
                    AND message.message_type = 'text'
                    AND message.status = 'pending'
                    AND message.correlation_deadline <= clock_timestamp()
                    AND NOT EXISTS (
                        SELECT 1
                        FROM notifications.inbound_whatsapp_messages AS correlated_image
                        WHERE correlated_image.correlated_text_id = message.id);
                RETURN FOUND;
            END;
            $function$;

            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.claim_inbound_whatsapp_message(uuid);
            CREATE FUNCTION notifications.claim_inbound_whatsapp_message(p_inbound_id uuid)
            RETURNS TABLE (status text, wa_id text, media_id text, caption text, user_text text, claimed boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_whatsapp_messages%ROWTYPE;
                v_user_text text;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_row
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::text, NULL::text, NULL::text, NULL::text, false;
                    RETURN;
                END IF;

                IF v_row.message_type <> 'image' OR v_row.status IN ('reply_in_flight', 'completed', 'failed') THEN
                    RETURN QUERY SELECT v_row.status, NULL::text, NULL::text, NULL::text, NULL::text, false;
                    RETURN;
                END IF;

                SELECT text_message.text_body INTO v_user_text
                FROM notifications.inbound_whatsapp_messages AS text_message
                WHERE text_message.id = v_row.correlated_text_id
                    AND text_message.message_type = 'text'
                    AND text_message.status = 'pending'
                    AND text_message.resolution_state = 'admitted'
                    AND text_message.phone_number_id = v_row.phone_number_id
                    AND text_message.wa_id = v_row.wa_id
                    AND text_message.resolved_user_id = v_row.resolved_user_id
                    AND text_message.resolved_company_id = v_row.resolved_company_id
                    AND text_message.resolved_membership_id = v_row.resolved_membership_id;

                IF v_row.status = 'pending' THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET status = 'processing'
                    WHERE message.id = p_inbound_id;
                END IF;

                RETURN QUERY SELECT 'processing'::text, v_row.wa_id, v_row.media_id, v_row.caption, v_user_text, true;
            END;
            $function$;

            REVOKE ALL ON FUNCTION notifications.admit_inbound_whatsapp_text(uuid, text, text, text, text, text, uuid, uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.correlate_inbound_whatsapp_text_image(uuid)
                FROM PUBLIC, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.expire_inbound_whatsapp_text(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.admit_inbound_whatsapp_text(uuid, text, text, text, text, text, uuid, uuid, uuid)
                TO vinx_runtime;
            GRANT EXECUTE ON FUNCTION notifications.correlate_inbound_whatsapp_text_image(uuid)
                TO vinx_runtime, vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.expire_inbound_whatsapp_text(uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
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
                    WHERE message_type = 'text'
                        OR correlated_text_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'cannot downgrade inbound whatsapp text correlation while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_whatsapp_text(uuid, text, text, text, text, text, uuid, uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.correlate_inbound_whatsapp_text_image(uuid)
                FROM PUBLIC, vinx_runtime, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.expire_inbound_whatsapp_text(uuid)
                FROM PUBLIC, vinx_runtime, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_runtime, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.admit_inbound_whatsapp_text(uuid, text, text, text, text, text, uuid, uuid, uuid);
            DROP FUNCTION notifications.correlate_inbound_whatsapp_text_image(uuid);
            DROP FUNCTION notifications.expire_inbound_whatsapp_text(uuid);
            DROP FUNCTION notifications.claim_inbound_whatsapp_message(uuid);
            CREATE FUNCTION notifications.claim_inbound_whatsapp_message(p_inbound_id uuid)
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
                SELECT * INTO v_row FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id FOR UPDATE;
                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::text, NULL::text, NULL::text, false;
                    RETURN;
                END IF;
                IF v_row.status IN ('reply_in_flight', 'completed', 'failed') THEN
                    RETURN QUERY SELECT v_row.status, NULL::text, NULL::text, NULL::text, false;
                    RETURN;
                END IF;
                IF v_row.status = 'pending' THEN
                    UPDATE notifications.inbound_whatsapp_messages SET status = 'processing' WHERE id = p_inbound_id;
                END IF;
                RETURN QUERY SELECT 'processing'::text, v_row.wa_id, v_row.media_id, v_row.caption, true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_message(uuid) TO vinx_worker;

            CREATE OR REPLACE FUNCTION notifications.admit_inbound_whatsapp_media(
                p_inbound_id uuid,
                p_phone_number_id text,
                p_message_id text,
                p_wa_id text,
                p_message_type text,
                p_media_id text,
                p_caption text,
                p_resolution_state text,
                p_user_id uuid,
                p_company_id uuid,
                p_membership_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_whatsapp_messages%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_phone_number_id IS NULL OR btrim(p_phone_number_id) = '' OR char_length(p_phone_number_id) > 100
                    OR p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$'
                    OR p_media_id IS NULL OR btrim(p_media_id) = '' OR char_length(p_media_id) > 200
                    OR (p_caption IS NOT NULL AND (btrim(p_caption) = '' OR char_length(p_caption) > 1000))
                    OR p_message_type IS NULL OR p_message_type NOT IN ('image', 'document')
                    OR p_resolution_state IS NULL
                        OR p_resolution_state NOT IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')
                    OR (p_resolution_state = 'admitted') <> (
                        p_user_id IS NOT NULL
                        AND p_company_id IS NOT NULL
                        AND p_membership_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'inbound whatsapp media admission parameters are invalid' USING ERRCODE = '22023';
                END IF;

                INSERT INTO notifications.inbound_whatsapp_messages (
                    id, phone_number_id, message_id, wa_id, message_type, media_id, caption, status,
                    resolution_state, resolved_user_id, resolved_company_id, resolved_membership_id, resolved_at)
                VALUES (
                    p_inbound_id, p_phone_number_id, p_message_id, p_wa_id, p_message_type, p_media_id, p_caption, 'pending',
                    p_resolution_state, p_user_id, p_company_id, p_membership_id, clock_timestamp())
                ON CONFLICT (phone_number_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN 'created';
                END IF;

                SELECT * INTO v_existing
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.phone_number_id = p_phone_number_id
                    AND message.message_id = p_message_id;

                IF v_existing.wa_id = p_wa_id
                    AND v_existing.message_type = p_message_type
                    AND v_existing.media_id = p_media_id THEN
                    RETURN 'replay';
                END IF;

                RETURN 'conflict';
            END;
            $function$;

            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT uq_inbound_whatsapp_messages_correlated_text,
                DROP CONSTRAINT fk_inbound_whatsapp_messages_correlated_text,
                DROP CONSTRAINT ck_inbound_whatsapp_messages_correlated_text,
                DROP CONSTRAINT ck_inbound_whatsapp_messages_correlation_deadline,
                DROP CONSTRAINT ck_inbound_whatsapp_messages_content,
                DROP CONSTRAINT inbound_whatsapp_messages_failure_reason_check,
                DROP CONSTRAINT inbound_whatsapp_messages_message_type_check,
                DROP COLUMN text_body,
                DROP COLUMN correlated_text_id,
                DROP COLUMN correlation_deadline,
                ALTER COLUMN media_id SET NOT NULL,
                ADD CONSTRAINT inbound_whatsapp_messages_message_type_check
                    CHECK (message_type IN ('image', 'document')),
                ADD CONSTRAINT inbound_whatsapp_messages_failure_reason_check
                    CHECK (failure_reason IS NULL OR failure_reason IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing'));
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
