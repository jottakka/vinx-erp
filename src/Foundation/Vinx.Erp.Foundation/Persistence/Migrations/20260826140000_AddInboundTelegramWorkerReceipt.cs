using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260826140000_AddInboundTelegramWorkerReceipt")]
public partial class AddInboundTelegramWorkerReceipt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            CREATE FUNCTION notifications.complete_inbound_telegram_text(
                p_inbound_id uuid)
            RETURNS TABLE (status text, transitioned boolean)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_status text;
                v_resolution_state text;
            BEGIN
                IF p_inbound_id IS NULL THEN
                    RAISE EXCEPTION 'inbound telegram text receipt id is required' USING ERRCODE = '22023';
                END IF;

                SELECT message.status, message.resolution_state
                    INTO v_status, v_resolution_state
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.id = p_inbound_id
                FOR UPDATE;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT 'not_found'::text, false;
                    RETURN;
                END IF;

                IF v_resolution_state <> 'admitted' THEN
                    RETURN QUERY SELECT 'not_admitted'::text, false;
                    RETURN;
                END IF;

                IF v_status <> 'pending' THEN
                    RETURN QUERY SELECT 'completed'::text, false;
                    RETURN;
                END IF;

                UPDATE notifications.inbound_telegram_messages AS message
                    SET status = 'completed', completed_at = clock_timestamp()
                    WHERE message.id = p_inbound_id
                        AND message.status = 'pending'
                        AND message.resolution_state = 'admitted';

                RETURN QUERY SELECT 'completed'::text, true;
            END;
            $function$;
            COMMENT ON FUNCTION notifications.complete_inbound_telegram_text(uuid) IS
                'Registra somente o recibo técnico da fila Telegram; não confirma resposta humana nem processa o texto.';
            ALTER FUNCTION notifications.complete_inbound_telegram_text(uuid)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.complete_inbound_telegram_text(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.complete_inbound_telegram_text(uuid)
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

            REVOKE ALL ON FUNCTION notifications.complete_inbound_telegram_text(uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.complete_inbound_telegram_text(uuid);

            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """
        );
}
