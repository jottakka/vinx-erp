using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260819140000_AddNotificationInboxReadState")]
public partial class AddNotificationInboxReadState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_authorization_owner;
            GRANT EXECUTE ON FUNCTION foundation.current_actor_memberships()
                TO vinx_notifications_owner;
            RESET ROLE;
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            ALTER TABLE notifications.notification_intents
                ADD COLUMN read_at timestamp with time zone NULL,
                ADD COLUMN read_by_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                ADD CONSTRAINT notification_intents_read_pair_check CHECK (
                    (read_at IS NULL) = (read_by_user_id IS NULL)),
                ADD CONSTRAINT notification_intents_read_in_app_check CHECK (
                    read_at IS NULL OR channel = 'in_app'),
                ADD CONSTRAINT notification_intents_read_recipient_check CHECK (
                    read_by_user_id IS NULL OR read_by_user_id = recipient_user_id),
                ADD CONSTRAINT notification_intents_read_delivered_check CHECK (
                    read_at IS NULL OR status = 'delivered');
            CREATE INDEX ix_notification_intents_inbox_page
                ON notifications.notification_intents (recipient_user_id, created_at DESC, id DESC)
                WHERE channel = 'in_app' AND status = 'delivered';
            CREATE INDEX ix_notification_intents_inbox_unread
                ON notifications.notification_intents (recipient_user_id, company_id)
                WHERE channel = 'in_app' AND status = 'delivered' AND read_at IS NULL;

            CREATE FUNCTION notifications.list_inbox_notifications(
                p_limit integer,
                p_cursor_created_at timestamp with time zone,
                p_cursor_id uuid)
            RETURNS TABLE (id uuid, company_id uuid, event_key text, deep_link_selector text,
                created_at timestamp with time zone, read_at timestamp with time zone)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_window integer;
            BEGIN
                IF v_actor_user_id IS NULL THEN
                    RAISE EXCEPTION 'notification actor is required' USING ERRCODE = '42501';
                END IF;
                IF p_limit IS NULL OR p_limit < 1 OR p_limit > 50 THEN
                    RAISE EXCEPTION 'inbox parameters are invalid' USING ERRCODE = '22023';
                END IF;
                IF (p_cursor_created_at IS NULL) <> (p_cursor_id IS NULL) THEN
                    RAISE EXCEPTION 'inbox parameters are invalid' USING ERRCODE = '22023';
                END IF;

                v_window := p_limit + 1;
                RETURN QUERY
                SELECT intent.id, intent.company_id, intent.event_key, intent.deep_link_selector,
                    intent.created_at, intent.read_at
                FROM notifications.notification_intents AS intent
                WHERE intent.channel = 'in_app'
                    AND intent.status = 'delivered'
                    AND intent.recipient_user_id = v_actor_user_id
                    AND intent.company_id IN (
                        SELECT membership.company_id
                        FROM foundation.current_actor_memberships() AS membership)
                    AND (p_cursor_created_at IS NULL
                        OR intent.created_at < p_cursor_created_at
                        OR (intent.created_at = p_cursor_created_at AND intent.id < p_cursor_id))
                ORDER BY intent.created_at DESC, intent.id DESC
                LIMIT v_window;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.list_inbox_notifications(integer, timestamp with time zone, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.list_inbox_notifications(integer, timestamp with time zone, uuid)
                TO vinx_runtime;

            CREATE FUNCTION notifications.count_unread_inbox_notifications()
            RETURNS TABLE (unread_count bigint)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
            BEGIN
                IF v_actor_user_id IS NULL THEN
                    RAISE EXCEPTION 'notification actor is required' USING ERRCODE = '42501';
                END IF;

                RETURN QUERY
                SELECT count(*)::bigint
                FROM notifications.notification_intents AS intent
                WHERE intent.channel = 'in_app'
                    AND intent.status = 'delivered'
                    AND intent.read_at IS NULL
                    AND intent.recipient_user_id = v_actor_user_id
                    AND intent.company_id IN (
                        SELECT membership.company_id
                        FROM foundation.current_actor_memberships() AS membership);
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.count_unread_inbox_notifications()
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.count_unread_inbox_notifications()
                TO vinx_runtime;

            CREATE FUNCTION notifications.mark_inbox_notification_read(
                p_delivery_id uuid)
            RETURNS TABLE (status text, read boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_correlation_id uuid := COALESCE(NULLIF(current_setting('app.correlation_id', true), '')::uuid, gen_random_uuid());
                v_company_id uuid;
                v_read_at timestamp with time zone;
                v_changed integer;
            BEGIN
                IF v_actor_user_id IS NULL THEN
                    RAISE EXCEPTION 'notification actor is required' USING ERRCODE = '42501';
                END IF;
                IF p_delivery_id IS NULL THEN
                    RAISE EXCEPTION 'inbox parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT intent.company_id, intent.read_at
                    INTO v_company_id, v_read_at
                FROM notifications.notification_intents AS intent
                WHERE intent.id = p_delivery_id
                    AND intent.channel = 'in_app'
                    AND intent.status = 'delivered'
                    AND intent.recipient_user_id = v_actor_user_id
                    AND intent.company_id IN (
                        SELECT membership.company_id
                        FROM foundation.current_actor_memberships() AS membership)
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false;
                    RETURN;
                END IF;

                IF v_read_at IS NOT NULL THEN
                    RETURN QUERY SELECT 'already_read'::text, false;
                    RETURN;
                END IF;

                UPDATE notifications.notification_intents AS intent
                SET read_at = clock_timestamp(),
                    read_by_user_id = v_actor_user_id,
                    updated_at = clock_timestamp()
                WHERE intent.id = p_delivery_id
                    AND intent.read_at IS NULL;
                GET DIAGNOSTICS v_changed = ROW_COUNT;
                IF v_changed = 0 THEN
                    RETURN QUERY SELECT 'already_read'::text, false;
                    RETURN;
                END IF;

                PERFORM audit.append_event(
                    v_actor_user_id, 'company', v_company_id, 'web',
                    'notification.inbox.read', 'notification_intent', p_delivery_id,
                    'success', v_correlation_id, NULL,
                    jsonb_build_object('channel', 'in_app'));
                RETURN QUERY SELECT 'read'::text, true;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.mark_inbox_notification_read(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.mark_inbox_notification_read(uuid)
                TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            DO $block$
            BEGIN
                IF EXISTS (SELECT 1 FROM notifications.notification_intents WHERE read_at IS NOT NULL) THEN
                    RAISE EXCEPTION 'cannot downgrade notification inbox while reads exist' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.list_inbox_notifications(integer, timestamp with time zone, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.count_unread_inbox_notifications()
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.mark_inbox_notification_read(uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.list_inbox_notifications(integer, timestamp with time zone, uuid);
            DROP FUNCTION notifications.count_unread_inbox_notifications();
            DROP FUNCTION notifications.mark_inbox_notification_read(uuid);
            DROP INDEX notifications.ix_notification_intents_inbox_page;
            DROP INDEX notifications.ix_notification_intents_inbox_unread;
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_read_delivered_check,
                DROP CONSTRAINT notification_intents_read_recipient_check,
                DROP CONSTRAINT notification_intents_read_in_app_check,
                DROP CONSTRAINT notification_intents_read_pair_check,
                DROP COLUMN read_at,
                DROP COLUMN read_by_user_id;
            RESET ROLE;
            SET ROLE vinx_authorization_owner;
            REVOKE EXECUTE ON FUNCTION foundation.current_actor_memberships()
                FROM vinx_notifications_owner;
            RESET ROLE;
            """);
}
