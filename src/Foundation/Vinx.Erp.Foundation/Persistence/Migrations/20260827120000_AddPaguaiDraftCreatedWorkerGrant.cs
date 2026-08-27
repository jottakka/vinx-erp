using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260827120000_AddPaguaiDraftCreatedWorkerGrant")]
public partial class AddPaguaiDraftCreatedWorkerGrant : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT USAGE ON SCHEMA paguai TO vinx_notifications_owner;
            GRANT SELECT (id, company_id, requester_user_id) ON TABLE paguai.requests
                TO vinx_notifications_owner;
            CREATE POLICY paguai_requests_notifications_owner_read
                ON paguai.requests FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY capabilities_notifications_owner_paguai_approve_read
                ON foundation.capabilities FOR SELECT TO vinx_notifications_owner
                USING (name = 'paguai.draft.approve');

            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE FUNCTION notifications.enqueue_paguai_draft_created_notification(
                p_company_id uuid,
                p_draft_id uuid,
                p_requester_user_id uuid,
                p_correlation_id uuid)
            RETURNS TABLE (status text, delivery_id uuid)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, paguai, foundation, audit, pg_temp
            AS $function$
            DECLARE
                v_recipient_user_id uuid;
                v_delivery_id uuid;
                v_recipient_count integer := 0;
                v_created boolean := false;
                v_event_key text := 'paguai.' || replace(p_draft_id::text, '-', '') || '.draft-created';
                v_deep_link_selector text := '/paguai';
            BEGIN
                IF p_company_id IS NULL OR p_company_id = '00000000-0000-0000-0000-000000000000'::uuid
                    OR p_draft_id IS NULL OR p_draft_id = '00000000-0000-0000-0000-000000000000'::uuid
                    OR p_requester_user_id IS NULL OR p_requester_user_id = '00000000-0000-0000-0000-000000000000'::uuid
                    OR p_correlation_id IS NULL OR p_correlation_id = '00000000-0000-0000-0000-000000000000'::uuid
                THEN
                    RAISE EXCEPTION 'Paguai draft notification parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

                IF NOT EXISTS (
                    SELECT 1
                    FROM paguai.requests AS draft
                    WHERE draft.id = p_draft_id
                      AND draft.company_id = p_company_id
                      AND draft.requester_user_id = p_requester_user_id)
                THEN
                    RAISE EXCEPTION 'Paguai draft notification does not match its company and requester'
                        USING ERRCODE = '22023';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_templates AS template
                    WHERE template.company_id = p_company_id
                      AND template.channel = 'in_app'
                      AND template.template_key = 'paguai.draft-created'
                      AND template.is_approved)
                THEN
                    RETURN QUERY SELECT 'template_unavailable'::text, NULL::uuid;
                    RETURN;
                END IF;

                FOR v_recipient_user_id IN
                    SELECT DISTINCT membership.user_id
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.role_assignments AS assignment
                        ON assignment.company_id = membership.company_id
                       AND assignment.membership_id = membership.id
                       AND assignment.deleted_at IS NULL
                    INNER JOIN foundation.roles AS role_record
                        ON role_record.company_id = assignment.company_id
                       AND role_record.id = assignment.role_id
                       AND role_record.deleted_at IS NULL
                    INNER JOIN foundation.role_capabilities AS grant_record
                        ON grant_record.company_id = role_record.company_id
                       AND grant_record.role_id = role_record.id
                       AND grant_record.scope = 'company'
                       AND grant_record.deleted_at IS NULL
                    INNER JOIN foundation.capabilities AS capability
                        ON capability.id = grant_record.capability_id
                       AND capability.name = 'paguai.draft.approve'
                    WHERE membership.company_id = p_company_id
                      AND membership.user_id <> p_requester_user_id
                      AND membership.deleted_at IS NULL
                    ORDER BY membership.user_id
                LOOP
                    v_recipient_count := v_recipient_count + 1;
                    v_delivery_id := NULL;
                    INSERT INTO notifications.notification_intents (
                        id, company_id, recipient_user_id, event_key, channel, template_key,
                        deep_link_selector, status, correlation_id, created_by_user_id)
                    VALUES (
                        gen_random_uuid(), p_company_id, v_recipient_user_id, v_event_key, 'in_app',
                        'paguai.draft-created', v_deep_link_selector, 'pending_delivery', p_correlation_id,
                        p_requester_user_id)
                    ON CONFLICT (event_key, recipient_user_id, channel, template_key) DO NOTHING
                    RETURNING id INTO v_delivery_id;

                    IF v_delivery_id IS NOT NULL THEN
                        v_created := true;
                        PERFORM audit.append_event(
                            p_requester_user_id, 'company', p_company_id, 'worker',
                            'notification.intent.enqueue', 'notification_intent', v_delivery_id,
                            'success', p_correlation_id, NULL,
                            jsonb_build_object('channel', 'in_app', 'event_key', v_event_key));
                        RETURN QUERY SELECT 'created'::text, v_delivery_id;
                    END IF;
                END LOOP;

                IF v_recipient_count = 0 THEN
                    RETURN QUERY SELECT 'no_current_approvers'::text, NULL::uuid;
                ELSIF NOT v_created THEN
                    RETURN QUERY SELECT 'replay'::text, NULL::uuid;
                END IF;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.enqueue_paguai_draft_created_notification(
                uuid, uuid, uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.enqueue_paguai_draft_created_notification(
                uuid, uuid, uuid, uuid)
                TO vinx_worker;
            REVOKE EXECUTE ON FUNCTION notifications.enqueue_notification_intent(
                uuid, uuid, text, text, text, text)
                FROM vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.enqueue_paguai_draft_created_notification(
                uuid, uuid, uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.enqueue_paguai_draft_created_notification(uuid, uuid, uuid, uuid);
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;

            DROP POLICY capabilities_notifications_owner_paguai_approve_read ON foundation.capabilities;
            DROP POLICY paguai_requests_notifications_owner_read ON paguai.requests;
            REVOKE SELECT (id, company_id, requester_user_id) ON TABLE paguai.requests
                FROM vinx_notifications_owner;
            REVOKE USAGE ON SCHEMA paguai FROM vinx_notifications_owner;
            """
        );
}
