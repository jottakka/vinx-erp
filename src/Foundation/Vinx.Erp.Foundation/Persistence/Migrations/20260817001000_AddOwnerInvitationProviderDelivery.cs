using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class AddOwnerInvitationProviderDelivery : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA foundation TO vinx_provisioning_owner, vinx_worker_owner;
            GRANT SELECT (id, email) ON TABLE foundation.users TO vinx_worker_owner;

            SET ROLE vinx_provisioning_owner;
            ALTER TABLE foundation.pending_owner_invitations
                ADD COLUMN provider_attempt_id uuid NULL,
                ADD COLUMN provider_started_at timestamp with time zone NULL,
                ADD COLUMN provider_completed_at timestamp with time zone NULL,
                ADD COLUMN provider_issuer text NULL,
                ADD COLUMN provider_subject uuid NULL,
                ADD COLUMN provider_failure_reason varchar(50) NULL;
            ALTER TABLE foundation.pending_owner_invitations
                DROP CONSTRAINT ck_pending_owner_invitations_status;
            ALTER TABLE foundation.pending_owner_invitations
                ADD CONSTRAINT ck_pending_owner_invitations_status CHECK (
                    (status = 'pending_delivery'
                        AND provider_pending_at IS NULL
                        AND provider_attempt_id IS NULL
                        AND provider_started_at IS NULL
                        AND provider_completed_at IS NULL
                        AND provider_issuer IS NULL
                        AND provider_subject IS NULL
                        AND provider_failure_reason IS NULL)
                    OR (status = 'pending_provider'
                        AND provider_pending_at IS NOT NULL
                        AND provider_attempt_id IS NULL
                        AND provider_started_at IS NULL
                        AND provider_completed_at IS NULL
                        AND provider_issuer IS NULL
                        AND provider_subject IS NULL
                        AND provider_failure_reason IS NULL)
                    OR (status = 'provider_in_flight'
                        AND provider_pending_at IS NOT NULL
                        AND provider_attempt_id IS NOT NULL
                        AND provider_started_at IS NOT NULL
                        AND provider_completed_at IS NULL
                        AND provider_issuer IS NULL
                        AND provider_subject IS NULL
                        AND provider_failure_reason IS NULL)
                    OR (status = 'pending_acceptance'
                        AND provider_pending_at IS NOT NULL
                        AND provider_attempt_id IS NOT NULL
                        AND provider_started_at IS NOT NULL
                        AND provider_completed_at IS NOT NULL
                        AND provider_issuer IS NOT NULL
                        AND provider_subject IS NOT NULL
                        AND provider_failure_reason IS NULL)
                    OR (status = 'provider_failed'
                        AND provider_pending_at IS NOT NULL
                        AND provider_attempt_id IS NOT NULL
                        AND provider_started_at IS NOT NULL
                        AND provider_completed_at IS NOT NULL
                        AND provider_issuer IS NULL
                        AND provider_subject IS NULL
                        AND provider_failure_reason = 'provider_rejected')
                    OR (status = 'provider_ambiguous'
                        AND provider_pending_at IS NOT NULL
                        AND provider_attempt_id IS NOT NULL
                        AND provider_started_at IS NOT NULL
                        AND provider_completed_at IS NOT NULL
                        AND provider_issuer IS NULL
                        AND provider_subject IS NULL
                        AND provider_failure_reason = 'provider_ambiguous'));
            ALTER TABLE foundation.pending_owner_invitations
                ADD CONSTRAINT ck_pending_owner_invitations_provider_subject_not_empty
                CHECK (
                    provider_subject IS NULL
                    OR provider_subject <> '00000000-0000-0000-0000-000000000000'::uuid);
            GRANT UPDATE (provider_attempt_id, provider_started_at,
                provider_completed_at, provider_issuer, provider_subject, provider_failure_reason)
                ON TABLE foundation.pending_owner_invitations TO vinx_worker_owner;
            RESET ROLE;

            SET ROLE vinx_worker_owner;
            CREATE FUNCTION foundation.claim_owner_invitation_provider_delivery(
                p_invitation_id uuid,
                p_correlation_id uuid)
            RETURNS TABLE (status text, attempt_id uuid, owner_email text, claimed boolean)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_company_id uuid;
                v_invitation_user_id uuid;
                v_receipt_company_id uuid;
                v_receipt_owner_user_id uuid;
                v_status text;
                v_attempt_id uuid;
                v_owner_email text;
            BEGIN
                IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
                    RAISE EXCEPTION 'owner invitation provider delivery parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                SELECT invitation.company_id,
                    invitation.user_id,
                    invitation.status,
                    invitation.provider_attempt_id
                INTO v_company_id,
                    v_invitation_user_id,
                    v_status,
                    v_attempt_id
                FROM foundation.pending_owner_invitations AS invitation
                WHERE invitation.id = p_invitation_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::uuid, NULL::text, false;
                    RETURN;
                END IF;

                SELECT receipt.company_id, receipt.owner_user_id
                INTO v_receipt_company_id, v_receipt_owner_user_id
                FROM foundation.organization_provisioning_receipts AS receipt
                WHERE receipt.owner_invitation_id = p_invitation_id;

                IF NOT FOUND
                    OR v_company_id IS DISTINCT FROM v_receipt_company_id
                    OR v_invitation_user_id IS DISTINCT FROM v_receipt_owner_user_id THEN
                    RAISE EXCEPTION 'owner invitation provisioning receipt is inconsistent'
                        USING ERRCODE = '23514';
                END IF;

                IF v_status = 'pending_provider' THEN
                    v_attempt_id := gen_random_uuid();
                    UPDATE foundation.pending_owner_invitations AS invitation
                    SET status = 'provider_in_flight',
                        provider_attempt_id = v_attempt_id,
                        provider_started_at = pg_catalog.clock_timestamp()
                    WHERE invitation.id = p_invitation_id
                      AND invitation.status = 'pending_provider';

                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'owner invitation provider claim lost its row lock'
                            USING ERRCODE = '40001';
                    END IF;

                    SELECT user_record.email
                    INTO v_owner_email
                    FROM foundation.users AS user_record
                    WHERE user_record.id = v_invitation_user_id;

                    IF NOT FOUND OR v_owner_email IS NULL OR btrim(v_owner_email) = '' THEN
                        RAISE EXCEPTION 'owner invitation user email is unavailable'
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN QUERY SELECT 'provider_in_flight'::text, v_attempt_id, v_owner_email, true;
                    RETURN;
                END IF;

                RETURN QUERY SELECT v_status, v_attempt_id, NULL::text, false;
            END;
            $function$;

            CREATE FUNCTION foundation.finalize_owner_invitation_provider_delivery(
                p_invitation_id uuid,
                p_attempt_id uuid,
                p_correlation_id uuid,
                p_outcome text,
                p_provider_issuer text,
                p_provider_subject uuid,
                p_failure_reason text)
            RETURNS TABLE (status text, finalized boolean)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_company_id uuid;
                v_invitation_user_id uuid;
                v_receipt_company_id uuid;
                v_receipt_owner_user_id uuid;
                v_actor_user_id uuid;
                v_status text;
                v_current_attempt_id uuid;
                v_next_status text;
                v_audit_outcome text;
            BEGIN
                IF p_invitation_id IS NULL OR p_attempt_id IS NULL OR p_correlation_id IS NULL
                    OR p_outcome NOT IN ('success', 'failed', 'ambiguous') THEN
                    RAISE EXCEPTION 'owner invitation provider finalization parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                SELECT invitation.company_id,
                    invitation.user_id,
                    invitation.status,
                    invitation.provider_attempt_id
                INTO v_company_id,
                    v_invitation_user_id,
                    v_status,
                    v_current_attempt_id
                FROM foundation.pending_owner_invitations AS invitation
                WHERE invitation.id = p_invitation_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false;
                    RETURN;
                END IF;

                IF v_status IS DISTINCT FROM 'provider_in_flight'
                    OR v_current_attempt_id IS DISTINCT FROM p_attempt_id THEN
                    RETURN QUERY SELECT v_status, false;
                    RETURN;
                END IF;

                SELECT receipt.company_id, receipt.owner_user_id, receipt.actor_user_id
                INTO v_receipt_company_id, v_receipt_owner_user_id, v_actor_user_id
                FROM foundation.organization_provisioning_receipts AS receipt
                WHERE receipt.owner_invitation_id = p_invitation_id;

                IF NOT FOUND
                    OR v_company_id IS DISTINCT FROM v_receipt_company_id
                    OR v_invitation_user_id IS DISTINCT FROM v_receipt_owner_user_id THEN
                    RAISE EXCEPTION 'owner invitation provisioning receipt is inconsistent'
                        USING ERRCODE = '23514';
                END IF;

                IF p_outcome = 'success' THEN
                    IF p_provider_issuer IS NULL OR btrim(p_provider_issuer) = ''
                        OR p_provider_subject IS NULL OR p_failure_reason IS NOT NULL THEN
                        RAISE EXCEPTION 'owner invitation provider success is invalid'
                            USING ERRCODE = '22023';
                    END IF;
                    v_next_status := 'pending_acceptance';
                    v_audit_outcome := 'success';
                    UPDATE foundation.pending_owner_invitations AS invitation
                    SET status = v_next_status,
                        provider_completed_at = pg_catalog.clock_timestamp(),
                        provider_issuer = btrim(p_provider_issuer),
                        provider_subject = p_provider_subject
                    WHERE invitation.id = p_invitation_id;
                ELSE
                    IF p_provider_issuer IS NOT NULL OR p_provider_subject IS NOT NULL
                        OR (p_failure_reason IS DISTINCT FROM CASE p_outcome
                                WHEN 'failed' THEN 'provider_rejected'
                                ELSE 'provider_ambiguous'
                            END) THEN
                        RAISE EXCEPTION 'owner invitation provider failure is invalid'
                            USING ERRCODE = '22023';
                    END IF;
                    v_next_status := CASE p_outcome
                        WHEN 'failed' THEN 'provider_failed'
                        ELSE 'provider_ambiguous'
                    END;
                    v_audit_outcome := CASE p_outcome
                        WHEN 'failed' THEN 'failure'
                        ELSE 'unknown'
                    END;
                    UPDATE foundation.pending_owner_invitations AS invitation
                    SET status = v_next_status,
                        provider_completed_at = pg_catalog.clock_timestamp(),
                        provider_failure_reason = p_failure_reason
                    WHERE invitation.id = p_invitation_id;
                END IF;

                PERFORM audit.append_event(
                    v_actor_user_id,
                    'company',
                    v_company_id,
                    'worker',
                    'owner_invitation.delivery.provider',
                    'owner_invitation',
                    p_invitation_id,
                    v_audit_outcome,
                    p_correlation_id,
                    p_failure_reason,
                    '{}'::jsonb);
                RETURN QUERY SELECT v_next_status, true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION foundation.claim_owner_invitation_provider_delivery(uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION foundation.finalize_owner_invitation_provider_delivery(uuid, uuid, uuid, text, text, uuid, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.claim_owner_invitation_provider_delivery(uuid, uuid)
                TO vinx_worker;
            GRANT EXECUTE ON FUNCTION foundation.finalize_owner_invitation_provider_delivery(uuid, uuid, uuid, text, text, uuid, text)
                TO vinx_worker;
            RESET ROLE;

            REVOKE CREATE ON SCHEMA foundation FROM vinx_provisioning_owner, vinx_worker_owner;
            """
        );

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_audit_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                        SELECT 1
                        FROM audit.audit_events
                        WHERE source = 'worker'
                          AND action = 'owner_invitation.delivery.provider'
                          AND resource_type = 'owner_invitation') THEN
                    RAISE EXCEPTION 'cannot downgrade owner invitation provider delivery while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            RESET ROLE;

            SET ROLE vinx_worker_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM foundation.pending_owner_invitations
                    WHERE status NOT IN ('pending_delivery', 'pending_provider')) THEN
                    RAISE EXCEPTION 'cannot downgrade owner invitation provider delivery while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION foundation.claim_owner_invitation_provider_delivery(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION foundation.finalize_owner_invitation_provider_delivery(uuid, uuid, uuid, text, text, uuid, text)
                FROM PUBLIC, vinx_worker, vinx_runtime, vinx_migrator;
            DROP FUNCTION foundation.claim_owner_invitation_provider_delivery(uuid, uuid);
            DROP FUNCTION foundation.finalize_owner_invitation_provider_delivery(uuid, uuid, uuid, text, text, uuid, text);
            RESET ROLE;

            REVOKE SELECT (id, email) ON TABLE foundation.users FROM vinx_worker_owner;
            GRANT CREATE ON SCHEMA foundation TO vinx_provisioning_owner;
            SET ROLE vinx_provisioning_owner;
            REVOKE UPDATE (provider_attempt_id, provider_started_at,
                provider_completed_at, provider_issuer, provider_subject, provider_failure_reason)
                ON TABLE foundation.pending_owner_invitations FROM vinx_worker_owner;
            ALTER TABLE foundation.pending_owner_invitations
                DROP CONSTRAINT ck_pending_owner_invitations_status;
            ALTER TABLE foundation.pending_owner_invitations
                DROP CONSTRAINT ck_pending_owner_invitations_provider_subject_not_empty;
            ALTER TABLE foundation.pending_owner_invitations
                DROP COLUMN provider_attempt_id,
                DROP COLUMN provider_started_at,
                DROP COLUMN provider_completed_at,
                DROP COLUMN provider_issuer,
                DROP COLUMN provider_subject,
                DROP COLUMN provider_failure_reason;
            ALTER TABLE foundation.pending_owner_invitations
                ADD CONSTRAINT ck_pending_owner_invitations_status CHECK (
                    (status = 'pending_delivery' AND provider_pending_at IS NULL)
                    OR (status = 'pending_provider' AND provider_pending_at IS NOT NULL));
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_provisioning_owner;

            """
        );
}
