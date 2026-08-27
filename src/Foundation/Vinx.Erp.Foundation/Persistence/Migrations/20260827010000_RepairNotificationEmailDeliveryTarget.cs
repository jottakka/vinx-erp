using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260827010000_RepairNotificationEmailDeliveryTarget")]
public partial class RepairNotificationEmailDeliveryTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        ReplaceFunction(migrationBuilder, castEmailToText: true);

    // O downgrade restaura exatamente o contrato predecessor, inclusive a
    // incompatibilidade varchar/text, para manter o histórico EF reversível.
    // Owner, search_path e ACL worker-only permanecem fechados nos dois sentidos.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        ReplaceFunction(migrationBuilder, castEmailToText: false);

    private static void ReplaceFunction(
        MigrationBuilder migrationBuilder,
        bool castEmailToText
    ) =>
        migrationBuilder.Sql(
            $$"""
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            CREATE OR REPLACE FUNCTION notifications.get_notification_email_delivery_target(
                p_delivery_id uuid,
                p_attempt_id uuid)
            RETURNS TABLE (email text, subject text, body_html text, deep_link_selector text)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_delivery_id IS NULL OR p_attempt_id IS NULL THEN
                    RAISE EXCEPTION 'notification delivery parameters are invalid' USING ERRCODE = '22023';
                END IF;

                RETURN QUERY
                SELECT user_record.email{{(castEmailToText ? "::text" : string.Empty)}}, template.email_subject, template.email_body_html, intent.deep_link_selector
                FROM notifications.notification_intents AS intent
                JOIN foundation.users AS user_record
                    ON user_record.id = intent.recipient_user_id
                JOIN notifications.notification_templates AS template
                    ON template.company_id = intent.company_id
                    AND template.channel = intent.channel
                    AND template.template_key = intent.template_key
                WHERE intent.id = p_delivery_id
                    AND intent.status = 'provider_in_flight'
                    AND intent.provider_attempt_id = p_attempt_id
                    AND intent.channel = 'email'
                    AND template.is_approved
                    AND template.email_subject IS NOT NULL
                    AND template.email_body_html IS NOT NULL;

                IF NOT FOUND THEN
                    RETURN QUERY SELECT NULL::text, NULL::text, NULL::text, NULL::text;
                END IF;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.get_notification_email_delivery_target(uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.get_notification_email_delivery_target(uuid, uuid)
                TO vinx_worker;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """
        );
}
