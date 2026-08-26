using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260826130000_AddInboundTelegramTextAdmission")]
public partial class AddInboundTelegramTextAdmission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            CREATE TABLE notifications.inbound_telegram_messages (
                id uuid PRIMARY KEY,
                update_id bigint NOT NULL CHECK (update_id > 0),
                chat_id bigint NOT NULL CHECK (chat_id BETWEEN 1 AND 4503599627370495),
                message_id bigint NOT NULL CHECK (message_id > 0),
                text_body text NOT NULL CHECK (
                    btrim(text_body, E' \t\n\013\f\r') <> ''
                    AND char_length(text_body) BETWEEN 1 AND 4096),
                status text NOT NULL CHECK (status IN ('pending', 'completed')),
                resolution_state text NOT NULL CHECK (
                    resolution_state IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')),
                resolved_user_id uuid NULL,
                resolved_company_id uuid NULL,
                resolved_membership_id uuid NULL,
                resolved_at timestamp with time zone NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                completed_at timestamp with time zone NULL,
                CONSTRAINT ck_inbound_telegram_messages_resolution_identity CHECK (
                    (resolution_state = 'admitted'
                        AND resolved_user_id IS NOT NULL
                        AND resolved_company_id IS NOT NULL
                        AND resolved_membership_id IS NOT NULL)
                    OR (resolution_state IN ('unbound', 'ambiguous', 'unverified', 'inactive')
                        AND resolved_user_id IS NULL
                        AND resolved_company_id IS NULL
                        AND resolved_membership_id IS NULL)),
                CONSTRAINT ck_inbound_telegram_messages_completion CHECK (
                    (status = 'completed') = (completed_at IS NOT NULL)),
                CONSTRAINT uq_inbound_telegram_messages_chat_message UNIQUE (chat_id, message_id)
            );
            ALTER TABLE notifications.inbound_telegram_messages ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications.inbound_telegram_messages FORCE ROW LEVEL SECURITY;
            CREATE POLICY inbound_telegram_messages_owner_select ON notifications.inbound_telegram_messages
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY inbound_telegram_messages_owner_insert ON notifications.inbound_telegram_messages
                FOR INSERT TO vinx_notifications_owner WITH CHECK (true);
            CREATE POLICY inbound_telegram_messages_owner_update ON notifications.inbound_telegram_messages
                FOR UPDATE TO vinx_notifications_owner USING (true) WITH CHECK (true);

            CREATE FUNCTION notifications.resolve_inbound_telegram_actor(
                p_chat_id bigint)
            RETURNS TABLE (resolution_state text, user_id uuid, company_id uuid, membership_id uuid)
            LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_chat_id IS NULL OR p_chat_id NOT BETWEEN 1 AND 4503599627370495 THEN
                    RAISE EXCEPTION 'inbound telegram chat_id is invalid' USING ERRCODE = '22023';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram'
                        AND contact.address = p_chat_id::text
                ) THEN
                    RETURN QUERY
                        SELECT 'unbound'::text AS resolution_state,
                            NULL::uuid AS user_id,
                            NULL::uuid AS company_id,
                            NULL::uuid AS membership_id;
                    RETURN;
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'telegram'
                        AND contact.address = p_chat_id::text
                        AND contact.is_validated
                ) THEN
                    RETURN QUERY
                        SELECT 'unverified'::text AS resolution_state,
                            NULL::uuid AS user_id,
                            NULL::uuid AS company_id,
                            NULL::uuid AS membership_id;
                    RETURN;
                END IF;

                IF (
                    SELECT count(*)
                    FROM (
                        SELECT DISTINCT membership.id
                        FROM notifications.notification_recipient_contacts AS contact
                        JOIN foundation.memberships AS membership
                            ON membership.company_id = contact.company_id
                            AND membership.user_id = contact.user_id
                        JOIN foundation.companies AS company
                            ON company.id = contact.company_id
                        WHERE contact.channel = 'telegram'
                            AND contact.address = p_chat_id::text
                            AND contact.is_validated
                            AND membership.deleted_at IS NULL
                            AND company.deleted_at IS NULL
                    ) AS active_identities
                ) = 0 THEN
                    RETURN QUERY
                        SELECT 'inactive'::text AS resolution_state,
                            NULL::uuid AS user_id,
                            NULL::uuid AS company_id,
                            NULL::uuid AS membership_id;
                    RETURN;
                END IF;

                IF (
                    SELECT count(*)
                    FROM (
                        SELECT DISTINCT membership.id
                        FROM notifications.notification_recipient_contacts AS contact
                        JOIN foundation.memberships AS membership
                            ON membership.company_id = contact.company_id
                            AND membership.user_id = contact.user_id
                        JOIN foundation.companies AS company
                            ON company.id = contact.company_id
                        WHERE contact.channel = 'telegram'
                            AND contact.address = p_chat_id::text
                            AND contact.is_validated
                            AND membership.deleted_at IS NULL
                            AND company.deleted_at IS NULL
                    ) AS active_identities
                ) > 1 THEN
                    RETURN QUERY
                        SELECT 'ambiguous'::text AS resolution_state,
                            NULL::uuid AS user_id,
                            NULL::uuid AS company_id,
                            NULL::uuid AS membership_id;
                    RETURN;
                END IF;

                RETURN QUERY
                SELECT 'admitted'::text AS resolution_state,
                    active.user_id AS user_id,
                    active.company_id AS company_id,
                    active.membership_id AS membership_id
                FROM (
                    SELECT DISTINCT membership.user_id,
                        membership.company_id,
                        membership.id AS membership_id
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company
                        ON company.id = contact.company_id
                    WHERE contact.channel = 'telegram'
                        AND contact.address = p_chat_id::text
                        AND contact.is_validated
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL
                ) AS active
                LIMIT 1;
            END;
            $function$;
            ALTER FUNCTION notifications.resolve_inbound_telegram_actor(bigint)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.resolve_inbound_telegram_actor(bigint)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.resolve_inbound_telegram_actor(bigint)
                TO vinx_runtime;

            CREATE FUNCTION notifications.admit_inbound_telegram_text(
                p_inbound_id uuid,
                p_update_id bigint,
                p_chat_id bigint,
                p_message_id bigint,
                p_text_body text,
                p_resolution_state text,
                p_user_id uuid,
                p_company_id uuid,
                p_membership_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_telegram_messages%ROWTYPE;
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_update_id IS NULL OR p_update_id <= 0
                    OR p_chat_id IS NULL OR p_chat_id NOT BETWEEN 1 AND 4503599627370495
                    OR p_message_id IS NULL OR p_message_id <= 0
                    OR p_text_body IS NULL
                    OR btrim(p_text_body, E' \t\n\013\f\r') = ''
                    OR char_length(p_text_body) NOT BETWEEN 1 AND 4096
                    OR p_resolution_state IS NULL
                    OR p_resolution_state NOT IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')
                    OR NOT (
                        (p_resolution_state = 'admitted'
                            AND p_user_id IS NOT NULL
                            AND p_company_id IS NOT NULL
                            AND p_membership_id IS NOT NULL)
                        OR (p_resolution_state IN ('unbound', 'ambiguous', 'unverified', 'inactive')
                            AND p_user_id IS NULL
                            AND p_company_id IS NULL
                            AND p_membership_id IS NULL)) THEN
                    RAISE EXCEPTION 'inbound telegram text admission parameters are invalid' USING ERRCODE = '22023';
                END IF;

                INSERT INTO notifications.inbound_telegram_messages (
                    id, update_id, chat_id, message_id, text_body, status,
                    resolution_state, resolved_user_id, resolved_company_id, resolved_membership_id,
                    resolved_at, created_at)
                VALUES (
                    p_inbound_id, p_update_id, p_chat_id, p_message_id, p_text_body, 'pending',
                    p_resolution_state, p_user_id, p_company_id, p_membership_id,
                    v_now, v_now)
                ON CONFLICT (chat_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN 'created';
                END IF;

                SELECT message.* INTO v_existing
                FROM notifications.inbound_telegram_messages AS message
                WHERE message.chat_id = p_chat_id
                    AND message.message_id = p_message_id;

                IF v_existing.update_id = p_update_id
                    AND v_existing.text_body = p_text_body THEN
                    RETURN 'replay';
                END IF;

                RETURN 'conflict';
            END;
            $function$;
            ALTER FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text, text, uuid, uuid, uuid)
                OWNER TO vinx_notifications_owner;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text, text, uuid, uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text, text, uuid, uuid, uuid)
                TO vinx_runtime;

            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;

            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM notifications.inbound_telegram_messages AS message
                ) THEN
                    RAISE EXCEPTION 'cannot downgrade inbound telegram text admission while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            REVOKE ALL ON FUNCTION notifications.resolve_inbound_telegram_actor(bigint)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text, text, uuid, uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.resolve_inbound_telegram_actor(bigint);
            DROP FUNCTION notifications.admit_inbound_telegram_text(uuid, bigint, bigint, bigint, text, text, uuid, uuid, uuid);
            DROP TABLE notifications.inbound_telegram_messages;

            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            """);
}
