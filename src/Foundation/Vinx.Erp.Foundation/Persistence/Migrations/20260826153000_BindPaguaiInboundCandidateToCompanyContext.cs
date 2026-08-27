using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

public partial class BindPaguaiInboundCandidateToCompanyContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE OR REPLACE FUNCTION notifications.read_inbound_whatsapp_invoice_candidate(
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

                IF v_candidate.company_id IS DISTINCT FROM
                    NULLIF(current_setting('app.company_id', true), '')::uuid THEN
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

    // Down fail-closed: restaurar o corpo anterior reintroduziria a leitura
    // cross-company de candidatos inbound (ator multiempresa lendo candidato
    // de outra empresa que não a do GUC app.company_id). Correções de
    // segurança não são reversíveis por downgrade.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DO $block$
            BEGIN
                RAISE EXCEPTION 'cannot downgrade: read_inbound_whatsapp_invoice_candidate must keep the app.company_id binding'
                    USING ERRCODE = '0A000';
            END;
            $block$;
            """
        );
}
