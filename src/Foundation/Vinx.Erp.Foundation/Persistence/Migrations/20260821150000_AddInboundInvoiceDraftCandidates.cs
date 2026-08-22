using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

# nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260821150000_AddInboundInvoiceDraftCandidates")]
public partial class AddInboundInvoiceDraftCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE TABLE notifications.inbound_whatsapp_invoice_candidates (
                inbound_message_id uuid PRIMARY KEY
                    REFERENCES notifications.inbound_whatsapp_messages (id),
                company_id uuid NOT NULL,
                user_id uuid NOT NULL,
                state text NOT NULL CHECK (state IN ('candidate', 'needs_review')),
                beneficiary_name text NULL
                    CHECK (beneficiary_name IS NULL OR (beneficiary_name = btrim(beneficiary_name) AND char_length(beneficiary_name) BETWEEN 1 AND 200)),
                amount text NULL CHECK (amount IS NULL OR amount ~ '^[0-9]+(\.[0-9]+)?$'),
                currency text NULL CHECK (currency IS NULL OR currency = 'BRL'),
                due_date date NULL,
                document_number text NULL
                    CHECK (document_number IS NULL OR (document_number = btrim(document_number) AND char_length(document_number) BETWEEN 1 AND 100)),
                needs_review_reason text NULL
                    CHECK (needs_review_reason IS NULL OR needs_review_reason IN (
                        'missing_amount', 'invalid_amount', 'missing_beneficiary',
                        'missing_due_date', 'no_text_layer', 'extraction_unavailable')),
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT ck_inbound_invoice_candidates_closed_state CHECK (
                    (state = 'candidate') = (
                        beneficiary_name IS NOT NULL
                        AND amount IS NOT NULL
                        AND currency IS NOT NULL
                        AND due_date IS NOT NULL
                        AND needs_review_reason IS NULL)),
                CONSTRAINT ck_inbound_invoice_candidates_review_reason CHECK (
                    (state = 'needs_review') = (needs_review_reason IS NOT NULL))
            );
            ALTER TABLE notifications.inbound_whatsapp_invoice_candidates ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.inbound_whatsapp_invoice_candidates FORCE ROW LEVEL SECURITY;
            CREATE POLICY inbound_whatsapp_invoice_candidates_owner_select
                ON notifications.inbound_whatsapp_invoice_candidates
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY inbound_whatsapp_invoice_candidates_owner_insert
                ON notifications.inbound_whatsapp_invoice_candidates
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);

            -- Evolução do claim do PR2: a extração (#272 PR3) consume também a
            -- legenda da mensagem; o tipo de retorno muda, então drop + create.
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.claim_inbound_whatsapp_document(uuid);
            CREATE FUNCTION notifications.claim_inbound_whatsapp_document(
                p_inbound_id uuid)
            RETURNS TABLE (
                status text,
                wa_id text,
                media_id text,
                caption text,
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
                    RETURN QUERY SELECT 'not_found'::text, NULL::text, NULL::text, NULL::text,
                        NULL::uuid, NULL::uuid, NULL::uuid, false;
                    RETURN;
                END IF;

                IF v_row.status IN ('completed', 'failed') THEN
                    RETURN QUERY SELECT v_row.status::text, NULL::text, NULL::text, NULL::text,
                        NULL::uuid, NULL::uuid, NULL::uuid, false;
                    RETURN;
                END IF;

                IF v_row.status = 'pending' THEN
                    UPDATE notifications.inbound_whatsapp_messages AS message
                        SET status = 'processing'
                        WHERE message.id = p_inbound_id;
                END IF;

                RETURN QUERY SELECT 'processing'::text, v_row.wa_id, v_row.media_id, v_row.caption,
                    v_row.resolved_user_id, v_row.resolved_company_id, v_row.resolved_membership_id, true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.inbound_document_actor_is_allowed(
                p_inbound_id uuid)
            RETURNS boolean
            LANGUAGE plpgsql STABLE SECURITY DEFINER
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
                WHERE message.id = p_inbound_id;

                IF NOT FOUND THEN
                    RETURN false;
                END IF;

                RETURN EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE membership.id = v_row.resolved_membership_id
                        AND membership.company_id = v_row.resolved_company_id
                        AND membership.user_id = v_row.resolved_user_id
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL
                ) AND EXISTS (
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
                );
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.inbound_document_actor_is_allowed(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.inbound_document_actor_is_allowed(uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.record_inbound_whatsapp_invoice_candidate(
                p_inbound_id uuid,
                p_state text,
                p_beneficiary_name text,
                p_amount text,
                p_currency text,
                p_due_date date,
                p_document_number text,
                p_needs_review_reason text)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_document notifications.inbound_whatsapp_messages%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_state NOT IN ('candidate', 'needs_review')
                    OR (p_beneficiary_name IS NOT NULL
                        AND (btrim(p_beneficiary_name) = '' OR char_length(p_beneficiary_name) > 200))
                    OR (p_amount IS NOT NULL AND p_amount !~ '^[0-9]+(\.[0-9]+)?$')
                    OR (p_currency IS NOT NULL AND p_currency <> 'BRL')
                    OR (p_document_number IS NOT NULL
                        AND (btrim(p_document_number) = '' OR char_length(p_document_number) > 100))
                    OR (p_state = 'candidate'
                        AND (p_beneficiary_name IS NULL OR p_amount IS NULL
                            OR p_currency IS NULL OR p_due_date IS NULL
                            OR p_needs_review_reason IS NOT NULL))
                    OR (p_state = 'needs_review'
                        AND (p_needs_review_reason IS NULL OR p_beneficiary_name IS NOT NULL
                            OR p_amount IS NOT NULL OR p_currency IS NOT NULL OR p_due_date IS NOT NULL)
                        AND p_needs_review_reason NOT IN (
                            'missing_amount', 'invalid_amount', 'missing_beneficiary',
                            'missing_due_date', 'no_text_layer', 'extraction_unavailable')) THEN
                    RAISE EXCEPTION 'inbound whatsapp invoice candidate parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT * INTO v_document
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.id = p_inbound_id;

                IF NOT FOUND THEN
                    RETURN 'not_found';
                END IF;

                INSERT INTO notifications.inbound_whatsapp_invoice_candidates (
                    inbound_message_id, company_id, user_id, state,
                    beneficiary_name, amount, currency, due_date, document_number, needs_review_reason)
                VALUES (
                    p_inbound_id, v_document.resolved_company_id, v_document.resolved_user_id, p_state,
                    p_beneficiary_name, p_amount, p_currency, p_due_date, p_document_number,
                    p_needs_review_reason)
                ON CONFLICT (inbound_message_id) DO NOTHING;
                RETURN CASE WHEN FOUND THEN 'created' ELSE 'replay' END;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_invoice_candidate(uuid, text, text, text, text, date, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.record_inbound_whatsapp_invoice_candidate(uuid, text, text, text, text, date, text, text)
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
                IF EXISTS (SELECT 1 FROM notifications.inbound_whatsapp_invoice_candidates) THEN
                    RAISE EXCEPTION 'cannot downgrade inbound invoice candidates while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.inbound_document_actor_is_allowed(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_invoice_candidate(uuid, text, text, text, text, date, text, text)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.inbound_document_actor_is_allowed(uuid);
            DROP FUNCTION notifications.record_inbound_whatsapp_invoice_candidate(uuid, text, text, text, text, date, text, text);
            DROP TABLE notifications.inbound_whatsapp_invoice_candidates;
            -- Restaura o claim do PR2 (sem caption).
            REVOKE ALL ON FUNCTION notifications.claim_inbound_whatsapp_document(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.claim_inbound_whatsapp_document(uuid);
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
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
