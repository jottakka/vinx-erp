using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260826121000_AddTelegramOutboundTarget")]
public partial class AddTelegramOutboundTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            CREATE FUNCTION notifications.get_telegram_notification_delivery_target(
                p_delivery_id uuid,
                p_attempt_id uuid)
            RETURNS TABLE (chat_id text, message_text text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_delivery_id IS NULL OR p_attempt_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                RETURN QUERY
                SELECT contact.address, template.telegram_text
                FROM notifications.notification_intents AS intent
                JOIN notifications.notification_recipient_contacts AS contact
                    ON contact.company_id = intent.company_id
                    AND contact.user_id = intent.recipient_user_id
                    AND contact.channel = intent.channel
                JOIN notifications.notification_templates AS template
                    ON template.company_id = intent.company_id
                    AND template.channel = intent.channel
                    AND template.template_key = intent.template_key
                WHERE intent.id = p_delivery_id
                    AND intent.channel = 'telegram'
                    AND intent.status = 'provider_in_flight'
                    AND intent.provider_attempt_id = p_attempt_id
                    AND contact.is_validated
                    AND template.is_approved
                    AND template.telegram_text IS NOT NULL
                    AND btrim(template.telegram_text) <> '';

                IF NOT FOUND THEN
                    RETURN QUERY SELECT NULL::text, NULL::text;
                END IF;
            END;
            $function$;
            ALTER FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                TO vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_worker, vinx_migrator;
            DROP FUNCTION notifications.get_telegram_notification_delivery_target(uuid, uuid);
            RESET ROLE;
            """);
}
