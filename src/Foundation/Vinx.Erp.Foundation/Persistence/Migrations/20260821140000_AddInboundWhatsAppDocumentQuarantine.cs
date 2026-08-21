using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260821140000_AddInboundWhatsAppDocumentQuarantine")]
public partial class AddInboundWhatsAppDocumentQuarantine : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            -- O re-check de capability no complete lê o catálogo de papéis como
            -- vinx_notifications_owner (padrão provisioning/employee directory):
            -- grants de leitura estreitos + policies RLS por papel owner.
            GRANT SELECT (id, name) ON foundation.capabilities TO vinx_notifications_owner;
            GRANT SELECT ON foundation.roles, foundation.role_assignments,
                foundation.role_capabilities TO vinx_notifications_owner;
            CREATE POLICY capabilities_notifications_owner_read ON foundation.capabilities
                FOR SELECT TO vinx_notifications_owner
                USING (name = 'foundation.files.upload');
            CREATE POLICY roles_notifications_owner_read ON foundation.roles
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY role_assignments_notifications_owner_read ON foundation.role_assignments
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY role_capabilities_notifications_owner_read ON foundation.role_capabilities
                FOR SELECT TO vinx_notifications_owner USING (true);

            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT inbound_whatsapp_messages_failure_reason_check;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD CONSTRAINT inbound_whatsapp_messages_failure_reason_check
                    CHECK (failure_reason IS NULL OR failure_reason IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing'));

            CREATE TABLE notifications.inbound_whatsapp_documents (
                inbound_message_id uuid PRIMARY KEY
                    REFERENCES notifications.inbound_whatsapp_messages (id),
                company_id uuid NOT NULL,
                user_id uuid NOT NULL,
                membership_id uuid NOT NULL,
                mime_type text NOT NULL
                    CHECK (mime_type IN ('application/pdf', 'image/jpeg', 'image/png')),
                size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 26214400),
                checksum text NOT NULL CHECK (checksum ~ '^sha256:[0-9a-f]{64}$'),
                page_count integer NULL CHECK (page_count BETWEEN 1 AND 20),
                scan_status text NOT NULL CHECK (scan_status IN ('clean')),
                scan_engine text NULL CHECK (scan_engine IS NULL OR char_length(scan_engine) <= 100),
                scan_engine_version text NULL CHECK (scan_engine_version IS NULL OR char_length(scan_engine_version) <= 50),
                scan_signature_version text NULL CHECK (scan_signature_version IS NULL OR char_length(scan_signature_version) <= 50),
                scanned_at timestamp with time zone NOT NULL,
                provider_key text NOT NULL
                    CHECK (provider_key = btrim(provider_key) AND char_length(provider_key) BETWEEN 5 AND 300),
                state text NOT NULL DEFAULT 'admitted' CHECK (state IN ('admitted')),
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
            );
            ALTER TABLE notifications.inbound_whatsapp_documents ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.inbound_whatsapp_documents FORCE ROW LEVEL SECURITY;
            CREATE POLICY inbound_whatsapp_documents_owner_select ON notifications.inbound_whatsapp_documents
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY inbound_whatsapp_documents_owner_insert ON notifications.inbound_whatsapp_documents
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);

            CREATE FUNCTION notifications.claim_inbound_whatsapp_document(
                p_inbound_id uuid)
            RETURNS TABLE (
                status text,
                wa_id text,
                media_id text,
                resolved_user_id uuid,
                resolved_company_id uuid,
                resolved_membership_id uuid,
                claimed boolean)
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

                IF NOT FOUND OR v_row.message_type <> 'document' OR v_row.resolution_state <> 'admitted' THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::text, NULL::text,
                        NULL::uuid, NULL::uuid, NULL::uuid, false;
                    RETURN;
                END IF;

                IF v_row.status IN ('completed', 'failed') THEN
                    RETURN QUERY SELECT v_row.status::text, NULL::text, NULL::text,
                        NULL::uuid, NULL::uuid, NULL::uuid, false;
                    RETURN;
                END IF;

                IF v_row.status = 'pending' THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET status = 'processing'
                        WHERE message.id = p_inbound_id;
                END IF;

                RETURN QUERY SELECT 'processing'::text, v_row.wa_id, v_row.media_id,
                    v_row.resolved_user_id, v_row.resolved_company_id, v_row.resolved_membership_id, true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.complete_inbound_whatsapp_document(
                p_inbound_id uuid,
                p_mime_type text,
                p_size_bytes bigint,
                p_checksum text,
                p_page_count integer,
                p_scan_engine text,
                p_scan_engine_version text,
                p_scan_signature_version text,
                p_scanned_at timestamptz,
                p_provider_key text,
                p_correlation_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_whatsapp_messages%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_mime_type NOT IN ('application/pdf', 'image/jpeg', 'image/png')
                    OR p_size_bytes IS NULL OR p_size_bytes NOT BETWEEN 1 AND 26214400
                    OR p_checksum IS NULL OR p_checksum !~ '^sha256:[0-9a-f]{64}$'
                    OR (p_page_count IS NOT NULL AND p_page_count NOT BETWEEN 1 AND 20)
                    OR p_scanned_at IS NULL
                    OR p_provider_key IS NULL OR btrim(p_provider_key) = ''
                        OR char_length(p_provider_key) > 300
                    OR p_correlation_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp document completion parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_row
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF NOT FOUND OR v_row.status <> 'processing'
                    OR v_row.message_type <> 'document'
                    OR v_row.resolution_state <> 'admitted' THEN
                    RETURN 'not_found';
                END IF;

                -- Fechado: a admissão só vira documento quando a membership
                -- segue ativa em empresa ativa e o remetente conserva a
                -- capability de depositar arquivos na empresa.
                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE membership.id = v_row.resolved_membership_id
                        AND membership.company_id = v_row.resolved_company_id
                        AND membership.user_id = v_row.resolved_user_id
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL
                ) OR NOT EXISTS (
                    SELECT 1
                    FROM foundation.role_assignments AS assignment
                    JOIN foundation.roles AS role ON role.id = assignment.role_id
                    JOIN foundation.role_capabilities AS role_capability
                        ON role_capability.role_id = role.id
                    JOIN foundation.capabilities AS capability
                        ON capability.id = role_capability.capability_id
                    WHERE assignment.company_id = v_row.resolved_company_id
                        AND assignment.membership_id = v_row.resolved_membership_id
                        AND assignment.deleted_at IS NULL
                        AND role_capability.deleted_at IS NULL
                        AND role_capability.scope = 'company'
                        AND capability.name = 'foundation.files.upload'
                ) THEN
                    RETURN 'forbidden';
                END IF;

                INSERT INTO notifications.inbound_whatsapp_documents (
                    inbound_message_id, company_id, user_id, membership_id,
                    mime_type, size_bytes, checksum, page_count,
                    scan_status, scan_engine, scan_engine_version, scan_signature_version,
                    scanned_at, provider_key)
                VALUES (
                    p_inbound_id, v_row.resolved_company_id, v_row.resolved_user_id,
                    v_row.resolved_membership_id, p_mime_type, p_size_bytes, p_checksum,
                    p_page_count, 'clean', p_scan_engine, p_scan_engine_version,
                    p_scan_signature_version, p_scanned_at, p_provider_key)
                ON CONFLICT (inbound_message_id) DO NOTHING;

                UPDATE notifications.inbound_whatsapp_messages AS message
                    SET status = 'completed',
                        replied_at = clock_timestamp()
                    WHERE message.id = p_inbound_id
                        AND message.status = 'processing';

                -- Auditoria com contexto mínimo: identificadores opacos, sem
                -- telefone, legenda, nome de arquivo ou conteúdo.
                PERFORM audit.append_event(
                    v_row.resolved_user_id, 'company', v_row.resolved_company_id, 'worker',
                    'notification.whatsapp_document.admitted', 'inbound_whatsapp_document',
                    p_inbound_id, 'success', p_correlation_id, NULL,
                    jsonb_build_object('channel', 'whatsapp', 'message_type', 'document'));
                RETURN 'completed';
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_whatsapp_document(uuid, text, bigint, text, integer, text, text, text, timestamp with time zone, text, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.complete_inbound_whatsapp_document(uuid, text, bigint, text, integer, text, text, text, timestamp with time zone, text, uuid)
                TO vinx_worker;

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
                IF EXISTS (SELECT 1 FROM notifications.inbound_whatsapp_documents) THEN
                    RAISE EXCEPTION 'cannot downgrade inbound whatsapp document quarantine while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_whatsapp_document(uuid, text, bigint, text, integer, text, text, text, timestamp with time zone, text, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.claim_inbound_whatsapp_document(uuid);
            DROP FUNCTION notifications.complete_inbound_whatsapp_document(uuid, text, bigint, text, integer, text, text, text, timestamp with time zone, text, uuid);
            DROP TABLE notifications.inbound_whatsapp_documents;
            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT inbound_whatsapp_messages_failure_reason_check;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD CONSTRAINT inbound_whatsapp_messages_failure_reason_check
                    CHECK (failure_reason IS NULL OR failure_reason IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous'));

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
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            DROP POLICY capabilities_notifications_owner_read ON foundation.capabilities;
            DROP POLICY roles_notifications_owner_read ON foundation.roles;
            DROP POLICY role_assignments_notifications_owner_read ON foundation.role_assignments;
            DROP POLICY role_capabilities_notifications_owner_read ON foundation.role_capabilities;
            REVOKE SELECT (id, name) ON foundation.capabilities FROM vinx_notifications_owner;
            REVOKE SELECT ON foundation.roles, foundation.role_assignments,
                foundation.role_capabilities FROM vinx_notifications_owner;
            """);
}
