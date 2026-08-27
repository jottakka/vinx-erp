using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260827080300_AddWhatsAppAgentAdapter")]
public partial class AddWhatsAppAgentAdapter : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT SELECT ON TABLE foundation.companies, foundation.groups,
                foundation.memberships, foundation.roles, foundation.role_assignments,
                foundation.role_capabilities, foundation.capabilities, foundation.files,
                foundation.agent_execution_ledger, foundation.users TO vinx_worker;
            GRANT INSERT, UPDATE ON TABLE foundation.files TO vinx_worker;
            GRANT REFERENCES (id) ON TABLE foundation.files TO vinx_notifications_owner;
            GRANT SELECT (
                id, company_id, uploader_membership_id, processing_state,
                classification, deleted_at)
                ON TABLE foundation.files TO vinx_notifications_owner;
            GRANT SELECT (
                company_id, actor_user_id, channel, inbound_message_id,
                request_id, journey, state)
                ON TABLE foundation.agent_execution_ledger TO vinx_notifications_owner;
            GRANT INSERT ON TABLE foundation.agent_execution_ledger TO vinx_worker;
            GRANT UPDATE (state, safe_result_json, reason_code, updated_at, row_version)
                ON TABLE foundation.agent_execution_ledger TO vinx_worker;
            GRANT USAGE ON SCHEMA audit TO vinx_worker;
            GRANT SELECT, INSERT ON TABLE paguai.draft_proposals TO vinx_worker;
            GRANT UPDATE (state, result_draft_id, resolution_request_id, resolved_at, row_version)
                ON TABLE paguai.draft_proposals TO vinx_worker;
            SET ROLE vinx_authorization_owner;
            GRANT EXECUTE ON FUNCTION foundation.current_actor_memberships() TO vinx_worker;
            RESET ROLE;
            SET ROLE vinx_audit_owner;
            GRANT EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                TO vinx_worker;
            RESET ROLE;

            DROP POLICY capabilities_notifications_owner_read ON foundation.capabilities;
            CREATE POLICY capabilities_notifications_owner_read ON foundation.capabilities
                FOR SELECT TO vinx_notifications_owner
                USING (name IN (
                    'foundation.files.upload',
                    'foundation.files.read-ocr',
                    'ai.read',
                    'paguai.draft.create'));

            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD COLUMN agent_status text NOT NULL DEFAULT 'agent_pending'
                    CHECK (agent_status IN (
                        'agent_pending', 'agent_in_flight', 'agent_completed',
                        'agent_failed', 'agent_ambiguous')),
                ADD COLUMN agent_failure_reason text NULL
                    CHECK (agent_failure_reason IS NULL OR agent_failure_reason IN (
                        'capability_missing', 'media_unavailable', 'document_rejected',
                        'document_scan_unavailable', 'document_storage_unavailable',
                        'agent_failed', 'agent_ambiguous')),
                ADD COLUMN agent_file_id uuid NULL,
                ADD COLUMN agent_started_at timestamp with time zone NULL,
                ADD COLUMN agent_completed_at timestamp with time zone NULL,
                ADD CONSTRAINT fk_inbound_whatsapp_messages_agent_file
                    FOREIGN KEY (agent_file_id) REFERENCES foundation.files(id),
                ADD CONSTRAINT ck_inbound_whatsapp_messages_agent_terminal CHECK (
                    (agent_status = 'agent_pending'
                        AND agent_started_at IS NULL
                        AND agent_completed_at IS NULL
                        AND agent_failure_reason IS NULL)
                    OR (agent_status = 'agent_in_flight'
                        AND agent_started_at IS NOT NULL
                        AND agent_completed_at IS NULL
                        AND agent_failure_reason IS NULL)
                    OR (agent_status = 'agent_completed'
                        AND agent_started_at IS NOT NULL
                        AND agent_completed_at IS NOT NULL
                        AND agent_failure_reason IS NULL)
                    OR (agent_status IN ('agent_failed', 'agent_ambiguous')
                        AND agent_started_at IS NOT NULL
                        AND agent_completed_at IS NOT NULL
                        AND agent_failure_reason IS NOT NULL));

            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT inbound_whatsapp_messages_failure_reason_check,
                ADD CONSTRAINT inbound_whatsapp_messages_failure_reason_check
                    CHECK (failure_reason IS NULL OR failure_reason IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing',
                        'correlation_timeout', 'correlation_ambiguous',
                        'agent_failed', 'agent_ambiguous'));

            CREATE FUNCTION notifications.claim_inbound_whatsapp_agent(p_inbound_id uuid)
            RETURNS TABLE (
                status text,
                claimed boolean,
                message_type text,
                wa_id text,
                media_id text,
                caption text,
                user_text text,
                resolved_user_id uuid,
                resolved_company_id uuid,
                resolved_membership_id uuid,
                agent_file_id uuid)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_row notifications.inbound_whatsapp_messages%ROWTYPE;
                v_user_text text;
                v_requires_file boolean;
                v_authorized boolean;
                v_terminal_recovery boolean;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_row
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF NOT FOUND OR v_row.resolution_state <> 'admitted' THEN
                    RETURN QUERY SELECT 'not_found'::text, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid,
                        NULL::uuid;
                    RETURN;
                END IF;

                v_requires_file := v_row.message_type IN ('image', 'document');
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
                            AND execution.channel = 'whatsapp'
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
                        NULL::text, NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid,
                        NULL::uuid;
                    RETURN;
                END IF;

                IF NOT v_terminal_recovery AND v_row.message_type = 'text' AND EXISTS (
                    SELECT 1
                    FROM notifications.inbound_whatsapp_messages AS image
                    WHERE image.correlated_text_id = v_row.id) THEN
                    RETURN QUERY SELECT 'correlated'::text, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid,
                        NULL::uuid;
                    RETURN;
                END IF;

                IF NOT v_terminal_recovery AND v_row.correlation_deadline > clock_timestamp()
                    AND (
                        v_row.message_type = 'text'
                        OR (
                            v_row.message_type = 'image'
                            AND v_row.caption IS NULL
                            AND v_row.correlated_text_id IS NULL)) THEN
                    RETURN QUERY SELECT 'correlation_pending'::text, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid,
                        NULL::uuid;
                    RETURN;
                END IF;

                SELECT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE membership.id = v_row.resolved_membership_id
                        AND membership.company_id = v_row.resolved_company_id
                        AND membership.user_id = v_row.resolved_user_id
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL)
                    AND EXISTS (
                        SELECT 1
                        FROM foundation.role_assignments AS assignment
                        JOIN foundation.roles AS role ON role.id = assignment.role_id
                        JOIN foundation.role_capabilities AS grant_row
                            ON grant_row.role_id = role.id AND grant_row.company_id = role.company_id
                        JOIN foundation.capabilities AS capability
                            ON capability.id = grant_row.capability_id
                        WHERE assignment.company_id = v_row.resolved_company_id
                            AND assignment.membership_id = v_row.resolved_membership_id
                            AND assignment.deleted_at IS NULL
                            AND role.deleted_at IS NULL
                            AND grant_row.deleted_at IS NULL
                            AND grant_row.scope = 'company'
                            AND capability.name = 'ai.read')
                    AND (NOT v_requires_file OR (
                        SELECT count(DISTINCT capability.name) = CASE
                            WHEN v_row.message_type = 'document' THEN 3 ELSE 2 END
                        FROM foundation.role_assignments AS assignment
                        JOIN foundation.roles AS role ON role.id = assignment.role_id
                        JOIN foundation.role_capabilities AS grant_row
                            ON grant_row.role_id = role.id AND grant_row.company_id = role.company_id
                        JOIN foundation.capabilities AS capability
                            ON capability.id = grant_row.capability_id
                        WHERE assignment.company_id = v_row.resolved_company_id
                            AND assignment.membership_id = v_row.resolved_membership_id
                            AND assignment.deleted_at IS NULL
                            AND role.deleted_at IS NULL
                            AND grant_row.deleted_at IS NULL
                            AND grant_row.scope = 'company'
                            AND (capability.name IN (
                                    'foundation.files.upload',
                                    'foundation.files.read-ocr')
                                OR (v_row.message_type = 'document'
                                    AND capability.name = 'paguai.draft.create'))))
                INTO v_authorized;

                IF NOT v_authorized THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                    SET agent_status = 'agent_failed',
                        agent_started_at = clock_timestamp(),
                        agent_completed_at = clock_timestamp(),
                        agent_failure_reason = 'capability_missing',
                        status = 'failed',
                        failure_reason = 'capability_missing',
                        replied_at = clock_timestamp()
                    WHERE message.id = p_inbound_id;
                    RETURN QUERY SELECT 'capability_missing'::text, false, NULL::text, NULL::text,
                        NULL::text, NULL::text, NULL::text, NULL::uuid, NULL::uuid, NULL::uuid,
                        NULL::uuid;
                    RETURN;
                END IF;

                IF v_row.message_type = 'image' AND v_row.correlated_text_id IS NOT NULL THEN
                    SELECT text_message.text_body INTO v_user_text
                    FROM notifications.inbound_whatsapp_messages AS text_message
                    WHERE text_message.id = v_row.correlated_text_id
                        AND text_message.message_type = 'text'
                        AND text_message.resolution_state = 'admitted'
                        AND text_message.resolved_user_id = v_row.resolved_user_id
                        AND text_message.resolved_company_id = v_row.resolved_company_id
                        AND text_message.resolved_membership_id = v_row.resolved_membership_id;
                ELSIF v_row.message_type = 'text' THEN
                    v_user_text := v_row.text_body;
                END IF;

                IF v_terminal_recovery THEN
                    RETURN QUERY SELECT 'terminal_recovery'::text, true, v_row.message_type,
                        v_row.wa_id, v_row.media_id, v_row.caption, v_user_text,
                        v_row.resolved_user_id, v_row.resolved_company_id,
                        v_row.resolved_membership_id, v_row.agent_file_id;
                    RETURN;
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                SET agent_status = 'agent_in_flight',
                    agent_started_at = clock_timestamp(),
                    status = CASE WHEN message.status = 'pending' THEN 'processing' ELSE message.status END
                WHERE message.id = p_inbound_id;

                RETURN QUERY SELECT 'agent_in_flight'::text, true, v_row.message_type,
                    v_row.wa_id, v_row.media_id, v_row.caption, v_user_text,
                    v_row.resolved_user_id, v_row.resolved_company_id,
                    v_row.resolved_membership_id, NULL::uuid;
            END;
            $function$;

            CREATE FUNCTION notifications.record_inbound_whatsapp_agent_file(
                p_inbound_id uuid,
                p_file_id uuid)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_user_id uuid;
                v_company_id uuid;
            BEGIN
                IF p_inbound_id IS NULL OR p_file_id IS NULL THEN
                    RAISE EXCEPTION 'inbound agent file parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT message.resolved_user_id, message.resolved_company_id
                INTO v_user_id, v_company_id
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id;
                IF NOT FOUND THEN
                    RETURN false;
                END IF;
                PERFORM set_config('app.user_id', v_user_id::text, true);
                PERFORM set_config('app.company_id', v_company_id::text, true);

                UPDATE notifications.inbound_whatsapp_messages AS message
                SET agent_file_id = p_file_id
                WHERE message.id = p_inbound_id
                    AND message.agent_status = 'agent_in_flight'
                    AND message.message_type IN ('image', 'document')
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

            CREATE FUNCTION notifications.release_inbound_whatsapp_agent(p_inbound_id uuid)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                SET agent_status = 'agent_pending',
                    agent_started_at = NULL,
                    status = CASE WHEN message.status = 'processing' THEN 'pending' ELSE message.status END
                WHERE message.id = p_inbound_id
                    AND message.agent_status = 'agent_in_flight'
                    AND message.agent_file_id IS NULL
                    AND message.status = 'processing'
                    AND NOT EXISTS (
                        SELECT 1 FROM foundation.agent_execution_ledger AS execution
                        WHERE execution.inbound_message_id = p_inbound_id);
                RETURN FOUND;
            END;
            $function$;

            CREATE FUNCTION notifications.finalize_inbound_whatsapp_agent(
                p_inbound_id uuid,
                p_result text,
                p_reason text)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_agent_status text;
                v_status text;
                v_target text;
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_result NOT IN ('completed', 'failed', 'ambiguous')
                    OR (p_result = 'completed') <> (p_reason IS NULL)
                    OR (p_reason IS NOT NULL AND p_reason NOT IN (
                        'capability_missing', 'media_unavailable', 'document_rejected',
                        'document_scan_unavailable', 'document_storage_unavailable',
                        'agent_failed', 'agent_ambiguous')) THEN
                    RAISE EXCEPTION 'inbound agent finalization parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT message.agent_status, message.status
                INTO v_agent_status, v_status
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;
                IF NOT FOUND THEN
                    RETURN false;
                END IF;

                v_target := 'agent_' || p_result;
                IF v_agent_status <> 'agent_in_flight' THEN
                    RETURN v_agent_status = v_target;
                END IF;

                UPDATE notifications.inbound_whatsapp_messages AS message
                SET agent_status = v_target,
                    agent_failure_reason = p_reason,
                    agent_completed_at = clock_timestamp(),
                    status = CASE
                        WHEN v_status = 'processing' AND p_result = 'completed' THEN 'completed'
                        WHEN v_status = 'processing' AND p_result IN ('failed', 'ambiguous') THEN 'failed'
                        ELSE v_status
                    END,
                    failure_reason = CASE
                        WHEN v_status = 'processing' AND p_result IN ('failed', 'ambiguous') THEN p_reason
                        ELSE message.failure_reason
                    END,
                    replied_at = CASE
                        WHEN v_status = 'processing' THEN clock_timestamp()
                        ELSE message.replied_at
                    END
                WHERE message.id = p_inbound_id;
                RETURN true;
            END;
            $function$;

            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_agent(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_agent_file(uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.release_inbound_whatsapp_agent(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.finalize_inbound_whatsapp_agent(uuid, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_agent(uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.record_inbound_whatsapp_agent_file(uuid, uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.release_inbound_whatsapp_agent(uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION notifications.finalize_inbound_whatsapp_agent(uuid, text, text)
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
                    SELECT 1 FROM notifications.inbound_whatsapp_messages
                    WHERE agent_status <> 'agent_pending' OR agent_file_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'cannot downgrade whatsapp agent adapter while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_agent(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_agent_file(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.release_inbound_whatsapp_agent(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.finalize_inbound_whatsapp_agent(uuid, text, text)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.claim_inbound_whatsapp_agent(uuid);
            DROP FUNCTION notifications.record_inbound_whatsapp_agent_file(uuid, uuid);
            DROP FUNCTION notifications.release_inbound_whatsapp_agent(uuid);
            DROP FUNCTION notifications.finalize_inbound_whatsapp_agent(uuid, text, text);
            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT fk_inbound_whatsapp_messages_agent_file,
                DROP CONSTRAINT ck_inbound_whatsapp_messages_agent_terminal,
                DROP COLUMN agent_status,
                DROP COLUMN agent_failure_reason,
                DROP COLUMN agent_file_id,
                DROP COLUMN agent_started_at,
                DROP COLUMN agent_completed_at,
                DROP CONSTRAINT inbound_whatsapp_messages_failure_reason_check,
                ADD CONSTRAINT inbound_whatsapp_messages_failure_reason_check
                    CHECK (failure_reason IS NULL OR failure_reason IN (
                        'ai_unconfigured', 'media_unavailable', 'media_invalid',
                        'ocr_failed', 'ai_failed', 'reply_rejected', 'reply_ambiguous',
                        'document_rejected', 'document_scan_unavailable',
                        'document_storage_unavailable', 'capability_missing',
                        'correlation_timeout', 'correlation_ambiguous'));
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            REVOKE REFERENCES (id) ON TABLE foundation.files FROM vinx_notifications_owner;
            REVOKE SELECT (
                id, company_id, uploader_membership_id, processing_state,
                classification, deleted_at)
                ON TABLE foundation.files FROM vinx_notifications_owner;
            REVOKE SELECT (
                company_id, actor_user_id, channel, inbound_message_id,
                request_id, journey, state)
                ON TABLE foundation.agent_execution_ledger FROM vinx_notifications_owner;

            DROP POLICY capabilities_notifications_owner_read ON foundation.capabilities;
            CREATE POLICY capabilities_notifications_owner_read ON foundation.capabilities
                FOR SELECT TO vinx_notifications_owner
                USING (name = 'foundation.files.upload');
            SET ROLE vinx_audit_owner;
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_worker;
            RESET ROLE;
            SET ROLE vinx_authorization_owner;
            REVOKE EXECUTE ON FUNCTION foundation.current_actor_memberships() FROM vinx_worker;
            RESET ROLE;
            REVOKE SELECT, INSERT ON TABLE paguai.draft_proposals FROM vinx_worker;
            REVOKE UPDATE (state, result_draft_id, resolution_request_id, resolved_at, row_version)
                ON TABLE paguai.draft_proposals FROM vinx_worker;
            REVOKE USAGE ON SCHEMA audit FROM vinx_worker;
            REVOKE INSERT, UPDATE ON TABLE foundation.files FROM vinx_worker;
            REVOKE SELECT ON TABLE foundation.companies, foundation.groups,
                foundation.memberships, foundation.roles, foundation.role_assignments,
                foundation.role_capabilities, foundation.capabilities, foundation.files,
                foundation.agent_execution_ledger, foundation.users FROM vinx_worker;
            REVOKE INSERT ON TABLE foundation.agent_execution_ledger FROM vinx_worker;
            REVOKE UPDATE (state, safe_result_json, reason_code, updated_at, row_version)
                ON TABLE foundation.agent_execution_ledger FROM vinx_worker;
            """);
}
