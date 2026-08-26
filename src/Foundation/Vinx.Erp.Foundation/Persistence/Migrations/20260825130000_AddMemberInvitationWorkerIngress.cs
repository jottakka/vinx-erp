using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMemberInvitationWorkerIngress : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner, vinx_worker_owner;

            SET ROLE vinx_permissions_owner;
            ALTER TABLE foundation.pending_member_invitations
                ADD COLUMN provider_pending_at timestamp with time zone NULL;
            ALTER TABLE foundation.pending_member_invitations
                DROP CONSTRAINT pending_member_invitations_status_check;
            ALTER TABLE foundation.pending_member_invitations
                ADD CONSTRAINT ck_pending_member_invitations_status CHECK (
                    (status = 'pending_delivery' AND provider_pending_at IS NULL)
                    OR (status = 'pending_provider' AND provider_pending_at IS NOT NULL));
            CREATE INDEX ix_pending_member_invitations_provider_backlog
                ON foundation.pending_member_invitations (provider_pending_at, id)
                WHERE status = 'pending_provider';

            GRANT SELECT, UPDATE (status, provider_pending_at)
                ON TABLE foundation.pending_member_invitations TO vinx_worker_owner;
            CREATE POLICY pending_member_invitations_worker_owner_read
                ON foundation.pending_member_invitations FOR SELECT TO vinx_worker_owner USING (true);
            CREATE POLICY pending_member_invitations_worker_owner_update
                ON foundation.pending_member_invitations FOR UPDATE TO vinx_worker_owner
                USING (true) WITH CHECK (true);
            RESET ROLE;

            GRANT SELECT ON TABLE foundation.memberships TO vinx_worker_owner;
            CREATE POLICY memberships_worker_owner_read
                ON foundation.memberships FOR SELECT TO vinx_worker_owner
                USING (deleted_at IS NULL);

            SET ROLE vinx_worker_owner;
            CREATE FUNCTION foundation.accept_member_invitation_delivery(
                p_invitation_id uuid,
                p_correlation_id uuid)
            RETURNS TABLE (status text, provider_pending_at timestamp with time zone, transitioned boolean)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_company_id uuid;
                v_membership_id uuid;
                v_status text;
                v_provider_pending_at timestamp with time zone;
                v_membership_company_id uuid;
                v_actor_user_id uuid;
            BEGIN
                IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
                    RAISE EXCEPTION 'member invitation delivery parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                SELECT invitation.company_id,
                    invitation.membership_id,
                    invitation.status,
                    invitation.provider_pending_at
                INTO v_company_id,
                    v_membership_id,
                    v_status,
                    v_provider_pending_at
                FROM foundation.pending_member_invitations AS invitation
                WHERE invitation.id = p_invitation_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::timestamp with time zone, false;
                    RETURN;
                END IF;

                SELECT membership.company_id,
                    membership.created_by_user_id
                INTO v_membership_company_id,
                    v_actor_user_id
                FROM foundation.memberships AS membership
                WHERE membership.id = v_membership_id
                  AND membership.deleted_at IS NULL;

                IF NOT FOUND
                    OR v_membership_company_id IS DISTINCT FROM v_company_id THEN
                    RAISE EXCEPTION 'member invitation membership is inconsistent'
                        USING ERRCODE = '23514';
                END IF;

                IF v_status = 'pending_delivery' THEN
                    v_provider_pending_at := pg_catalog.clock_timestamp();
                    UPDATE foundation.pending_member_invitations AS invitation
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
                            'member_invitation.delivery.ingress',
                            'member_invitation',
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
            REVOKE ALL ON FUNCTION foundation.accept_member_invitation_delivery(uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.accept_member_invitation_delivery(uuid, uuid)
                TO vinx_worker;
            RESET ROLE;

            REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner, vinx_worker_owner;
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
                    FROM foundation.pending_member_invitations
                    WHERE status = 'pending_provider') THEN
                    RAISE EXCEPTION 'cannot downgrade member invitation worker ingress while provider-pending invitations exist'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION foundation.accept_member_invitation_delivery(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_runtime, vinx_migrator;
            DROP FUNCTION foundation.accept_member_invitation_delivery(uuid, uuid);
            RESET ROLE;

            SET ROLE vinx_audit_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM audit.audit_events
                    WHERE source = 'worker'
                      AND action = 'member_invitation.delivery.ingress'
                      AND resource_type = 'member_invitation') THEN
                    RAISE EXCEPTION 'cannot downgrade member invitation worker ingress while worker audit exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            RESET ROLE;

            DROP POLICY memberships_worker_owner_read ON foundation.memberships;
            REVOKE SELECT ON TABLE foundation.memberships FROM vinx_worker_owner;

            GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner;
            SET ROLE vinx_permissions_owner;
            DROP POLICY pending_member_invitations_worker_owner_update
                ON foundation.pending_member_invitations;
            DROP POLICY pending_member_invitations_worker_owner_read
                ON foundation.pending_member_invitations;
            REVOKE SELECT, UPDATE (status, provider_pending_at)
                ON TABLE foundation.pending_member_invitations FROM vinx_worker_owner;
            DROP INDEX foundation.ix_pending_member_invitations_provider_backlog;
            ALTER TABLE foundation.pending_member_invitations
                DROP CONSTRAINT ck_pending_member_invitations_status;
            ALTER TABLE foundation.pending_member_invitations
                DROP COLUMN provider_pending_at;
            ALTER TABLE foundation.pending_member_invitations
                ADD CONSTRAINT pending_member_invitations_status_check CHECK (status = 'pending_delivery');
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
            """
        );
}
