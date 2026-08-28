using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <summary>
/// Identidade durável do scheduler de Demandas (uma linha de claim por cadeia,
/// fence por geração; restart/replica nunca acumulam cadeias paralelas) e
/// função worker-only estreita de intents de Demandas — a função genérica
/// notifications.enqueue_notification_intent permanece revogada do vinx_worker
/// (20260827120000); o worker enfileira apenas in_app com chaves demanda.*.
/// </summary>
[DbContext(typeof(FoundationDbContext))]
[Migration("20260828092000_AddDemandasWorkerSchedulerAndNotifications")]
public sealed class AddDemandasWorkerSchedulerAndNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE TABLE demandas.scheduler_claim (
                name varchar(100) PRIMARY KEY,
                generation bigint NOT NULL CHECK (generation >= 1),
                owner_host varchar(200) NOT NULL,
                lease_expires_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
            );

            -- Falha fechada por padrão: apenas o worker opera a linha de claim.
            ALTER TABLE demandas.scheduler_claim ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.scheduler_claim FORCE ROW LEVEL SECURITY;
            CREATE POLICY scheduler_claim_worker_access ON demandas.scheduler_claim
                TO vinx_worker
                USING (true)
                WITH CHECK (true);

            GRANT SELECT, INSERT, UPDATE ON demandas.scheduler_claim TO vinx_worker;

            -- Função worker-only estreita (espelho do precedente
            -- 20260827120000): SECURITY DEFINER do dono de notifications, canal
            -- único in_app, chaves de evento/template restritas a demanda.*, com
            -- as validações da função genérica (membro ativo + template aprovado)
            -- e auditoria de origem worker.
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE FUNCTION notifications.enqueue_demandas_notification_intent(
                p_company_id uuid,
                p_recipient_user_id uuid,
                p_event_key text,
                p_channel text,
                p_template_key text,
                p_deep_link_selector text)
            RETURNS TABLE (status text, delivery_id uuid)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_correlation_id uuid := COALESCE(NULLIF(current_setting('app.correlation_id', true), '')::uuid, gen_random_uuid());
                v_delivery_id uuid;
            BEGIN
                IF v_actor_user_id IS NULL THEN
                    RAISE EXCEPTION 'notification actor is required' USING ERRCODE = '42501';
                END IF;
                IF p_company_id IS NULL
                    OR p_recipient_user_id IS NULL
                    OR p_channel IS DISTINCT FROM 'in_app'
                    OR p_event_key IS NULL OR btrim(p_event_key) = '' OR char_length(btrim(p_event_key)) > 200
                        OR p_event_key <> btrim(p_event_key)
                        OR p_event_key NOT LIKE 'demanda.%'
                    OR p_template_key IS NULL OR btrim(p_template_key) = '' OR char_length(btrim(p_template_key)) > 100
                        OR p_template_key <> btrim(p_template_key)
                        OR p_template_key NOT LIKE 'demanda.%'
                    OR (p_deep_link_selector IS NOT NULL
                        AND (p_deep_link_selector NOT LIKE '/%' OR char_length(p_deep_link_selector) > 200))
                THEN
                    RAISE EXCEPTION 'notification intent parameters are invalid' USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

                IF NOT EXISTS (
                    SELECT 1 FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                        AND membership.user_id = p_recipient_user_id
                        AND membership.deleted_at IS NULL)
                THEN
                    RAISE EXCEPTION 'recipient is not a member of the company' USING ERRCODE = '42501';
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM notifications.notification_templates AS template
                    WHERE template.company_id = p_company_id
                        AND template.channel = 'in_app'
                        AND template.template_key = p_template_key
                        AND template.is_approved)
                THEN
                    RAISE EXCEPTION 'notification template is not approved' USING ERRCODE = '22023';
                END IF;

                INSERT INTO notifications.notification_intents (
                    id, company_id, recipient_user_id, event_key, channel, template_key,
                    deep_link_selector, status, correlation_id, created_by_user_id)
                VALUES (
                    gen_random_uuid(), p_company_id, p_recipient_user_id, p_event_key, 'in_app', p_template_key,
                    p_deep_link_selector, 'pending_delivery', v_correlation_id, v_actor_user_id)
                ON CONFLICT (event_key, recipient_user_id, channel, template_key) DO NOTHING
                RETURNING id INTO v_delivery_id;

                IF v_delivery_id IS NULL THEN
                    SELECT existing_intent.id INTO v_delivery_id
                    FROM notifications.notification_intents AS existing_intent
                    WHERE existing_intent.event_key = p_event_key
                        AND existing_intent.recipient_user_id = p_recipient_user_id
                        AND existing_intent.channel = 'in_app'
                        AND existing_intent.template_key = p_template_key;
                    RETURN QUERY SELECT 'replay'::text, v_delivery_id;
                    RETURN;
                END IF;

                PERFORM audit.append_event(
                    v_actor_user_id, 'company', p_company_id, 'worker', 'notification.intent.enqueue',
                    'notification_intent', v_delivery_id, 'success', v_correlation_id, NULL,
                    jsonb_build_object('channel', 'in_app', 'event_key', p_event_key));
                RETURN QUERY SELECT 'created'::text, v_delivery_id;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.enqueue_demandas_notification_intent(
                uuid, uuid, text, text, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.enqueue_demandas_notification_intent(
                uuid, uuid, text, text, text, text)
                TO vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.enqueue_demandas_notification_intent(
                uuid, uuid, text, text, text, text)
                FROM PUBLIC, vinx_worker, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.enqueue_demandas_notification_intent(
                uuid, uuid, text, text, text, text);
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;

            REVOKE ALL ON demandas.scheduler_claim FROM vinx_worker;
            DROP POLICY scheduler_claim_worker_access ON demandas.scheduler_claim;
            DROP TABLE demandas.scheduler_claim;
            """
        );
}
