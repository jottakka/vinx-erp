using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class AddOwnerInvitationWorkerIngress : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT USAGE ON SCHEMA foundation TO vinx_worker;
            GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_worker_owner;
            GRANT CREATE ON SCHEMA foundation TO vinx_provisioning_owner;

            SET ROLE vinx_provisioning_owner;
            ALTER TABLE foundation.pending_owner_invitations
                ADD COLUMN provider_pending_at timestamp with time zone NULL;
            ALTER TABLE foundation.pending_owner_invitations
                DROP CONSTRAINT pending_owner_invitations_status_check;
            ALTER TABLE foundation.pending_owner_invitations
                ADD CONSTRAINT ck_pending_owner_invitations_status CHECK (
                    (status = 'pending_delivery' AND provider_pending_at IS NULL)
                    OR (status = 'pending_provider' AND provider_pending_at IS NOT NULL));
            CREATE INDEX ix_pending_owner_invitations_provider_backlog
                ON foundation.pending_owner_invitations (provider_pending_at, id)
                WHERE status = 'pending_provider';
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM foundation.organization_provisioning_receipts
                    GROUP BY owner_invitation_id
                    HAVING count(*) > 1) THEN
                    RAISE EXCEPTION 'organization provisioning receipt invitations are not unique'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            ALTER TABLE foundation.organization_provisioning_receipts
                ADD CONSTRAINT uq_organization_provisioning_receipts_owner_invitation_id
                UNIQUE (owner_invitation_id);

            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_provisioning_owner;
            SET ROLE vinx_provisioning_owner;

            GRANT SELECT, UPDATE (status, provider_pending_at)
                ON TABLE foundation.pending_owner_invitations TO vinx_worker_owner;
            GRANT SELECT ON TABLE foundation.organization_provisioning_receipts TO vinx_worker_owner;
            CREATE POLICY pending_owner_invitations_worker_owner_read
                ON foundation.pending_owner_invitations FOR SELECT TO vinx_worker_owner USING (true);
            CREATE POLICY pending_owner_invitations_worker_owner_update
                ON foundation.pending_owner_invitations FOR UPDATE TO vinx_worker_owner
                USING (true) WITH CHECK (true);
            CREATE POLICY organization_provisioning_receipts_worker_owner_read
                ON foundation.organization_provisioning_receipts FOR SELECT TO vinx_worker_owner USING (true);
            RESET ROLE;

            GRANT USAGE ON SCHEMA audit TO vinx_worker_owner;
            SET ROLE vinx_audit_owner;
            GRANT EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                TO vinx_worker_owner;
            RESET ROLE;

            SET ROLE vinx_worker_owner;
            CREATE FUNCTION foundation.accept_owner_invitation_delivery(
                p_invitation_id uuid,
                p_correlation_id uuid)
            RETURNS TABLE (status text, provider_pending_at timestamp with time zone, transitioned boolean)
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
                v_provider_pending_at timestamp with time zone;
            BEGIN
                IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
                    RAISE EXCEPTION 'owner invitation delivery parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                SELECT invitation.company_id,
                    invitation.user_id,
                    invitation.status,
                    invitation.provider_pending_at
                INTO v_company_id,
                    v_invitation_user_id,
                    v_status,
                    v_provider_pending_at
                FROM foundation.pending_owner_invitations AS invitation
                WHERE invitation.id = p_invitation_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::timestamp with time zone, false;
                    RETURN;
                END IF;

                SELECT receipt.company_id,
                    receipt.owner_user_id,
                    receipt.actor_user_id
                INTO v_receipt_company_id,
                    v_receipt_owner_user_id,
                    v_actor_user_id
                FROM foundation.organization_provisioning_receipts AS receipt
                WHERE receipt.owner_invitation_id = p_invitation_id;

                IF NOT FOUND THEN
                    RAISE EXCEPTION 'owner invitation provisioning receipt is missing'
                        USING ERRCODE = '23514';
                END IF;

                IF v_company_id IS DISTINCT FROM v_receipt_company_id THEN
                    RAISE EXCEPTION 'owner invitation receipt company is inconsistent'
                        USING ERRCODE = '23514';
                END IF;

                IF v_invitation_user_id IS DISTINCT FROM v_receipt_owner_user_id THEN
                    RAISE EXCEPTION 'owner invitation receipt owner is inconsistent'
                        USING ERRCODE = '23514';
                END IF;

                IF v_status = 'pending_delivery' THEN
                    v_provider_pending_at := pg_catalog.clock_timestamp();
                    UPDATE foundation.pending_owner_invitations AS invitation
                    SET status = 'pending_provider',
                        provider_pending_at = v_provider_pending_at
                    WHERE invitation.id = p_invitation_id
                      AND invitation.status = 'pending_delivery';

                    IF FOUND THEN
                        PERFORM audit.append_event(
                            v_actor_user_id,
                            'company',
                            v_company_id,
                            'worker',
                            'owner_invitation.delivery.ingress',
                            'owner_invitation',
                            p_invitation_id,
                            'success',
                            p_correlation_id,
                            NULL,
                            '{}'::jsonb);
                        RETURN QUERY SELECT 'pending_provider'::text, v_provider_pending_at, true;
                    ELSE
                        RETURN QUERY SELECT v_status, v_provider_pending_at, false;
                    END IF;
                    RETURN;
                END IF;

                RETURN QUERY SELECT v_status, v_provider_pending_at, false;
            END;
            $function$;
            REVOKE ALL ON FUNCTION foundation.accept_owner_invitation_delivery(uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.accept_owner_invitation_delivery(uuid, uuid)
                TO vinx_worker;
            RESET ROLE;

            REVOKE CREATE ON SCHEMA foundation FROM vinx_worker_owner;
            """
        );

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_worker_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM foundation.pending_owner_invitations
                    WHERE status = 'pending_provider') THEN
                    RAISE EXCEPTION 'cannot downgrade worker ingress while provider-pending invitations exist'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION foundation.accept_owner_invitation_delivery(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_runtime, vinx_migrator;
            DROP FUNCTION foundation.accept_owner_invitation_delivery(uuid, uuid);
            RESET ROLE;

            SET ROLE vinx_audit_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM audit.audit_events
                    WHERE source = 'worker'
                      AND action = 'owner_invitation.delivery.ingress'
                      AND resource_type = 'owner_invitation') THEN
                    RAISE EXCEPTION 'cannot downgrade worker ingress while worker audit exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_worker_owner;
            RESET ROLE;
            REVOKE USAGE ON SCHEMA audit FROM vinx_worker_owner;

            SET ROLE vinx_provisioning_owner;
            DROP POLICY organization_provisioning_receipts_worker_owner_read
                ON foundation.organization_provisioning_receipts;
            DROP POLICY pending_owner_invitations_worker_owner_update
                ON foundation.pending_owner_invitations;
            DROP POLICY pending_owner_invitations_worker_owner_read
                ON foundation.pending_owner_invitations;
            REVOKE SELECT ON TABLE foundation.organization_provisioning_receipts FROM vinx_worker_owner;
            REVOKE SELECT, UPDATE (status, provider_pending_at)
                ON TABLE foundation.pending_owner_invitations FROM vinx_worker_owner;
            DROP INDEX foundation.ix_pending_owner_invitations_provider_backlog;
            ALTER TABLE foundation.organization_provisioning_receipts
                DROP CONSTRAINT uq_organization_provisioning_receipts_owner_invitation_id;
            ALTER TABLE foundation.pending_owner_invitations
                DROP CONSTRAINT ck_pending_owner_invitations_status;
            ALTER TABLE foundation.pending_owner_invitations
                DROP COLUMN provider_pending_at;
            ALTER TABLE foundation.pending_owner_invitations
                ADD CONSTRAINT pending_owner_invitations_status_check CHECK (status = 'pending_delivery');
            RESET ROLE;

            REVOKE USAGE ON SCHEMA foundation FROM vinx_worker, vinx_worker_owner;
            """
        );
}
