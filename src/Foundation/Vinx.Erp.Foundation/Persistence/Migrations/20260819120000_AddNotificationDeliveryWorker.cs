using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260819120000_AddNotificationDeliveryWorker")]
public partial class AddNotificationDeliveryWorker : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_status_check;
            ALTER TABLE notifications.notification_intents
                ADD CONSTRAINT notification_intents_status_check CHECK (
                    status IN (
                        'pending_delivery',
                        'pending_provider',
                        'provider_in_flight',
                        'delivered',
                        'provider_failed',
                        'provider_ambiguous'));
            ALTER TABLE notifications.notification_intents
                ADD COLUMN provider_attempt_id uuid NULL,
                ADD COLUMN provider_claimed_at timestamp with time zone NULL,
                ADD COLUMN provider_resolved_at timestamp with time zone NULL,
                ADD COLUMN failure_reason text NULL
                    CHECK (failure_reason IS NULL OR failure_reason IN ('provider_rejected', 'provider_ambiguous'));
            ALTER TABLE notifications.notification_intents
                ADD CONSTRAINT notification_intents_lifecycle_check CHECK (
                    (status = 'pending_delivery'
                        AND provider_attempt_id IS NULL AND provider_claimed_at IS NULL AND provider_resolved_at IS NULL)
                    OR (status = 'pending_provider'
                        AND provider_attempt_id IS NULL AND provider_claimed_at IS NULL AND provider_resolved_at IS NULL)
                    OR (status = 'provider_in_flight'
                        AND provider_attempt_id IS NOT NULL AND provider_claimed_at IS NOT NULL AND provider_resolved_at IS NULL)
                    OR (status = 'delivered'
                        AND provider_attempt_id IS NOT NULL AND provider_resolved_at IS NOT NULL AND failure_reason IS NULL)
                    OR (status IN ('provider_failed', 'provider_ambiguous')
                        AND provider_attempt_id IS NOT NULL AND provider_resolved_at IS NOT NULL AND failure_reason IS NOT NULL));

            SET ROLE vinx_notifications_owner;
            CREATE FUNCTION notifications.accept_notification_delivery(
                p_delivery_id uuid,
                p_correlation_id uuid)
            RETURNS TABLE (status text, transitioned boolean, channel text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_recipient_user_id uuid;
                v_company_id uuid;
                v_channel text;
                v_status text;
            BEGIN
                IF p_delivery_id IS NULL OR p_correlation_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT intent.recipient_user_id, intent.company_id, intent.channel, intent.status
                    INTO v_recipient_user_id, v_company_id, v_channel, v_status
                FROM notifications.notification_intents AS intent
                WHERE intent.id = p_delivery_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false, NULL::text;
                    RETURN;
                END IF;

                IF v_status = 'pending_delivery' THEN
                    UPDATE notifications.notification_intents
                        SET status = 'pending_provider', updated_at = clock_timestamp()
                        WHERE id = p_delivery_id AND status = 'pending_delivery';
                    IF FOUND THEN
                        PERFORM audit.append_event(
                            v_recipient_user_id, 'company', v_company_id, 'worker',
                            'notification.delivery.ingress', 'notification_intent', p_delivery_id,
                            'success', p_correlation_id, NULL,
                            jsonb_build_object('from', 'pending_delivery', 'to', 'pending_provider'));
                        RETURN QUERY SELECT 'pending_provider'::text, true, v_channel;
                        RETURN;
                    END IF;
                    SELECT intent.status INTO v_status
                    FROM notifications.notification_intents AS intent
                    WHERE intent.id = p_delivery_id;
                END IF;

                RETURN QUERY SELECT v_status, false, v_channel;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.accept_notification_delivery(uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.accept_notification_delivery(uuid, uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.claim_notification_delivery(
                p_delivery_id uuid)
            RETURNS TABLE (status text, attempt_id uuid, channel text, claimed boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
                v_channel text;
                v_attempt_id uuid;
            BEGIN
                IF p_delivery_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT intent.status, intent.channel, intent.provider_attempt_id
                    INTO v_status, v_channel, v_attempt_id
                FROM notifications.notification_intents AS intent
                WHERE intent.id = p_delivery_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, NULL::uuid, NULL::text, false;
                    RETURN;
                END IF;

                IF v_status = 'pending_provider' THEN
                    v_attempt_id := gen_random_uuid();
                    UPDATE notifications.notification_intents
                        SET status = 'provider_in_flight',
                            provider_attempt_id = v_attempt_id,
                            provider_claimed_at = clock_timestamp(),
                            attempt_count = attempt_count + 1,
                            updated_at = clock_timestamp()
                        WHERE id = p_delivery_id AND status = 'pending_provider';
                    IF FOUND THEN
                        RETURN QUERY SELECT 'provider_in_flight'::text, v_attempt_id, v_channel, true;
                        RETURN;
                    END IF;
                    SELECT intent.status, intent.provider_attempt_id INTO v_status, v_attempt_id
                    FROM notifications.notification_intents AS intent
                    WHERE intent.id = p_delivery_id;
                END IF;

                RETURN QUERY SELECT v_status, v_attempt_id, v_channel, false;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.claim_notification_delivery(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.claim_notification_delivery(uuid)
                TO vinx_worker;

            CREATE FUNCTION notifications.finalize_notification_delivery(
                p_delivery_id uuid,
                p_attempt_id uuid,
                p_correlation_id uuid,
                p_outcome text)
            RETURNS TABLE (status text, finalized boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_recipient_user_id uuid;
                v_company_id uuid;
                v_status text;
                v_current_attempt_id uuid;
                v_new_status text;
                v_new_reason text;
            BEGIN
                IF p_delivery_id IS NULL OR p_attempt_id IS NULL OR p_correlation_id IS NULL
                    OR p_outcome NOT IN ('delivered', 'failed', 'ambiguous') THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT intent.recipient_user_id, intent.company_id, intent.status, intent.provider_attempt_id
                    INTO v_recipient_user_id, v_company_id, v_status, v_current_attempt_id
                FROM notifications.notification_intents AS intent
                WHERE intent.id = p_delivery_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false;
                    RETURN;
                END IF;

                v_new_status := CASE p_outcome
                    WHEN 'delivered' THEN 'delivered'
                    WHEN 'failed' THEN 'provider_failed'
                    ELSE 'provider_ambiguous' END;
                v_new_reason := CASE p_outcome
                    WHEN 'delivered' THEN NULL
                    WHEN 'failed' THEN 'provider_rejected'
                    ELSE 'provider_ambiguous' END;

                IF v_status = 'provider_in_flight' AND v_current_attempt_id = p_attempt_id THEN
                    UPDATE notifications.notification_intents
                        SET status = v_new_status,
                            provider_resolved_at = clock_timestamp(),
                            failure_reason = v_new_reason,
                            updated_at = clock_timestamp()
                        WHERE id = p_delivery_id
                            AND status = 'provider_in_flight'
                            AND provider_attempt_id = p_attempt_id;
                    IF FOUND THEN
                        PERFORM audit.append_event(
                            v_recipient_user_id, 'company', v_company_id, 'worker',
                            'notification.delivery.provider', 'notification_intent', p_delivery_id,
                            'success', p_correlation_id, NULL,
                            jsonb_build_object('outcome', p_outcome));
                        RETURN QUERY SELECT v_new_status, true;
                        RETURN;
                    END IF;
                    SELECT intent.status INTO v_status
                    FROM notifications.notification_intents AS intent
                    WHERE intent.id = p_delivery_id;
                END IF;

                RETURN QUERY SELECT v_status, false;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.finalize_notification_delivery(uuid, uuid, uuid, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.finalize_notification_delivery(uuid, uuid, uuid, text)
                TO vinx_worker;

            CREATE FUNCTION notifications.release_notification_delivery(
                p_delivery_id uuid,
                p_attempt_id uuid)
            RETURNS TABLE (status text, released boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
                v_current_attempt_id uuid;
            BEGIN
                IF p_delivery_id IS NULL OR p_attempt_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                SELECT intent.status, intent.provider_attempt_id
                    INTO v_status, v_current_attempt_id
                FROM notifications.notification_intents AS intent
                WHERE intent.id = p_delivery_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false;
                    RETURN;
                END IF;

                IF v_status = 'provider_in_flight' AND v_current_attempt_id = p_attempt_id THEN
                    UPDATE notifications.notification_intents
                        SET status = 'pending_provider',
                            provider_attempt_id = NULL,
                            provider_claimed_at = NULL,
                            updated_at = clock_timestamp()
                        WHERE id = p_delivery_id
                            AND status = 'provider_in_flight'
                            AND provider_attempt_id = p_attempt_id;
                    IF FOUND THEN
                        RETURN QUERY SELECT 'pending_provider'::text, true;
                        RETURN;
                    END IF;
                    SELECT intent.status INTO v_status
                    FROM notifications.notification_intents AS intent
                    WHERE intent.id = p_delivery_id;
                END IF;

                RETURN QUERY SELECT v_status, false;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.release_notification_delivery(uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.release_notification_delivery(uuid, uuid)
                TO vinx_worker;
            RESET ROLE;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM notifications.notification_intents
                    WHERE status <> 'pending_delivery') THEN
                    RAISE EXCEPTION 'cannot downgrade notification delivery worker while deliveries exist' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.accept_notification_delivery(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.claim_notification_delivery(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.finalize_notification_delivery(uuid, uuid, uuid, text)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.accept_notification_delivery(uuid, uuid);
            DROP FUNCTION notifications.claim_notification_delivery(uuid);
            DROP FUNCTION notifications.finalize_notification_delivery(uuid, uuid, uuid, text);
            DROP FUNCTION notifications.release_notification_delivery(uuid, uuid);
            RESET ROLE;
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_lifecycle_check;
            ALTER TABLE notifications.notification_intents
                DROP COLUMN provider_attempt_id,
                DROP COLUMN provider_claimed_at,
                DROP COLUMN provider_resolved_at,
                DROP COLUMN failure_reason;
            ALTER TABLE notifications.notification_intents
                DROP CONSTRAINT notification_intents_status_check;
            ALTER TABLE notifications.notification_intents
                ADD CONSTRAINT notification_intents_status_check CHECK (status IN ('pending_delivery'));
            """);
}
