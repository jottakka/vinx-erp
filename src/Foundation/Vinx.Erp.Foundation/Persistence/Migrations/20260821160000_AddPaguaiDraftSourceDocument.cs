using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

public partial class AddPaguaiDraftSourceDocument : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            ALTER TABLE paguai.requests
                ADD COLUMN source_inbound_message_id uuid NULL;
            ALTER TABLE paguai.requests
                ADD CONSTRAINT uq_paguai_requests_source_inbound_message
                UNIQUE (source_inbound_message_id);

            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE FUNCTION notifications.read_inbound_whatsapp_invoice_candidate(
                p_inbound_id uuid)
            RETURNS TABLE (
                company_id uuid,
                state text,
                beneficiary_name text,
                amount text,
                currency text,
                due_date date)
            LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_candidate notifications.inbound_whatsapp_invoice_candidates%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound whatsapp message id is required' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_candidate
                FROM notifications.inbound_whatsapp_invoice_candidates AS candidate
                WHERE candidate.inbound_message_id = p_inbound_id;

                IF NOT FOUND THEN
                    RETURN;
                END IF;

                IF v_candidate.company_id NOT IN (
                    SELECT membership.company_id
                    FROM foundation.current_actor_memberships() AS membership) THEN
                    RETURN;
                END IF;

                RETURN QUERY SELECT v_candidate.company_id, v_candidate.state,
                    v_candidate.beneficiary_name, v_candidate.amount, v_candidate.currency,
                    v_candidate.due_date;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.read_inbound_whatsapp_invoice_candidate(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.read_inbound_whatsapp_invoice_candidate(uuid)
                TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM paguai.requests
                    WHERE source_inbound_message_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'cannot downgrade paguai source document while drafts reference inbound messages'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.read_inbound_whatsapp_invoice_candidate(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.read_inbound_whatsapp_invoice_candidate(uuid);
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            ALTER TABLE paguai.requests
                DROP CONSTRAINT uq_paguai_requests_source_inbound_message;
            ALTER TABLE paguai.requests
                DROP COLUMN source_inbound_message_id;
            """
        );
}
