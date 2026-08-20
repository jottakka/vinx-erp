using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260821130000_AddInboundWhatsAppMediaAdmission")]
public partial class AddInboundWhatsAppMediaAdmission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            -- A resolução de identidade lê memberships/empresas da Foundation a
            -- partir do schema notifications. As tabelas têm FORCE RLS: o padrão
            -- do repositório é conceder SELECT + política de leitura fechada ao
            -- papel owner sem login (como vinx_authorization_owner em
            -- current_actor_memberships e o employee directory).
            GRANT SELECT (id, company_id, user_id, deleted_at) ON foundation.memberships
                TO vinx_notifications_owner;
            GRANT SELECT (id, deleted_at) ON foundation.companies
                TO vinx_notifications_owner;
            CREATE POLICY memberships_notifications_owner_read ON foundation.memberships
                FOR SELECT TO vinx_notifications_owner USING (true);
            CREATE POLICY companies_notifications_owner_read ON foundation.companies
                FOR SELECT TO vinx_notifications_owner USING (true);
            GRANT CREATE ON SCHEMA notifications TO vinx_notifications_owner;
            SET ROLE vinx_notifications_owner;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD COLUMN phone_number_id text NOT NULL DEFAULT '',
                ADD COLUMN message_type text NOT NULL DEFAULT 'image'
                    CHECK (message_type IN ('image', 'document')),
                ADD COLUMN resolution_state text NULL
                    CHECK (resolution_state IS NULL OR resolution_state IN (
                        'admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')),
                ADD COLUMN resolved_user_id uuid NULL,
                ADD COLUMN resolved_company_id uuid NULL,
                ADD COLUMN resolved_membership_id uuid NULL,
                ADD COLUMN resolved_at timestamp with time zone NULL,
                ADD CONSTRAINT ck_inbound_whatsapp_messages_admitted_identity
                    CHECK (
                        resolution_state IS NULL
                        OR (resolution_state = 'admitted') = (
                            resolved_user_id IS NOT NULL
                            AND resolved_company_id IS NOT NULL
                            AND resolved_membership_id IS NOT NULL)),
                ADD CONSTRAINT uq_inbound_whatsapp_messages_channel_message
                    UNIQUE (phone_number_id, message_id);
            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT uq_inbound_whatsapp_messages_message_id;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ALTER COLUMN phone_number_id DROP DEFAULT;

            CREATE FUNCTION notifications.resolve_inbound_whatsapp_actor(
                p_wa_id text)
            RETURNS TABLE (resolution_state text, user_id uuid, company_id uuid, membership_id uuid)
            LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$' THEN
                    RAISE EXCEPTION 'inbound whatsapp wa_id is invalid' USING ERRCODE = '22023';
                END IF;

                -- Nenhum contato vinculado ao número: remetente desconhecido.
                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'whatsapp'
                        AND contact.phone_number = '+' || p_wa_id
                ) THEN
                    RETURN QUERY SELECT 'unbound'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                -- Contato presente, mas ainda não validado: como is_validated
                -- torna-se verdadeiro é política de vinculação pendente de
                -- aprovação; esta fronteira somente lê o estado atual.
                IF NOT EXISTS (
                    SELECT 1
                    FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.channel = 'whatsapp'
                        AND contact.phone_number = '+' || p_wa_id
                        AND contact.is_validated
                ) THEN
                    RETURN QUERY SELECT 'unverified'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                IF (SELECT count(*) FROM (
                    SELECT DISTINCT membership.id
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company
                        ON company.id = contact.company_id
                    WHERE contact.channel = 'whatsapp'
                        AND contact.phone_number = '+' || p_wa_id
                        AND contact.is_validated
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL
                ) AS active_identities) = 0 THEN
                    RETURN QUERY SELECT 'inactive'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                -- Identidade múltipla para o mesmo número: nunca escolher
                -- automaticamente usuário, empresa ou tenant.
                IF (SELECT count(*) FROM (
                    SELECT DISTINCT membership.id
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company
                        ON company.id = contact.company_id
                    WHERE contact.channel = 'whatsapp'
                        AND contact.phone_number = '+' || p_wa_id
                        AND contact.is_validated
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL
                ) AS active_identities) > 1 THEN
                    RETURN QUERY SELECT 'ambiguous'::text, NULL::uuid, NULL::uuid, NULL::uuid;
                    RETURN;
                END IF;

                RETURN QUERY
                SELECT 'admitted'::text, active.user_id, active.company_id, active.membership_id
                FROM (
                    SELECT DISTINCT membership.user_id, membership.company_id, membership.id AS membership_id
                    FROM notifications.notification_recipient_contacts AS contact
                    JOIN foundation.memberships AS membership
                        ON membership.company_id = contact.company_id
                        AND membership.user_id = contact.user_id
                    JOIN foundation.companies AS company
                        ON company.id = contact.company_id
                    WHERE contact.channel = 'whatsapp'
                        AND contact.phone_number = '+' || p_wa_id
                        AND contact.is_validated
                        AND membership.deleted_at IS NULL
                        AND company.deleted_at IS NULL
                ) AS active
                LIMIT 1;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.resolve_inbound_whatsapp_actor(text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.resolve_inbound_whatsapp_actor(text)
                TO vinx_runtime;

            CREATE FUNCTION notifications.admit_inbound_whatsapp_media(
                p_inbound_id uuid,
                p_phone_number_id text,
                p_message_id text,
                p_wa_id text,
                p_message_type text,
                p_media_id text,
                p_caption text,
                p_resolution_state text,
                p_user_id uuid,
                p_company_id uuid,
                p_membership_id uuid)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            DECLARE
                v_existing notifications.inbound_whatsapp_messages%ROWTYPE;
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_phone_number_id IS NULL OR btrim(p_phone_number_id) = '' OR char_length(p_phone_number_id) > 100
                    OR p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$'
                    OR p_media_id IS NULL OR btrim(p_media_id) = '' OR char_length(p_media_id) > 200
                    OR (p_caption IS NOT NULL AND (btrim(p_caption) = '' OR char_length(p_caption) > 1000))
                    OR p_message_type IS NULL OR p_message_type NOT IN ('image', 'document')
                    OR p_resolution_state IS NULL
                        OR p_resolution_state NOT IN ('admitted', 'unbound', 'ambiguous', 'unverified', 'inactive')
                    OR (p_resolution_state = 'admitted') <> (
                        p_user_id IS NOT NULL
                        AND p_company_id IS NOT NULL
                        AND p_membership_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'inbound whatsapp media admission parameters are invalid' USING ERRCODE = '22023';
                END IF;

                -- Sem auditoria: o webhook não tem ator usuário e a linha
                -- deduplicada por (canal, message_id) é a evidência durável
                -- da admissão fechada (mesmo racional do #140/W3).
                INSERT INTO notifications.inbound_whatsapp_messages (
                    id, phone_number_id, message_id, wa_id, message_type, media_id, caption, status,
                    resolution_state, resolved_user_id, resolved_company_id, resolved_membership_id, resolved_at)
            VALUES (
                    p_inbound_id, p_phone_number_id, p_message_id, p_wa_id, p_message_type, p_media_id, p_caption, 'pending',
                    p_resolution_state, p_user_id, p_company_id, p_membership_id, clock_timestamp())
                ON CONFLICT (phone_number_id, message_id) DO NOTHING;
                IF FOUND THEN
                    RETURN 'created';
                END IF;

                SELECT * INTO v_existing
                FROM notifications.inbound_whatsapp_messages AS message
                WHERE message.phone_number_id = p_phone_number_id
                    AND message.message_id = p_message_id;

                IF v_existing.wa_id = p_wa_id
                    AND v_existing.message_type = p_message_type
                    AND v_existing.media_id = p_media_id THEN
                    RETURN 'replay';
                END IF;

                -- messageId reaproveitado com payload incompatível: conflito
                -- registrado de forma segura, sem duplicar o inbound.
                RETURN 'conflict';
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_whatsapp_media(uuid, text, text, text, text, text, text, text, uuid, uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.admit_inbound_whatsapp_media(uuid, text, text, text, text, text, text, text, uuid, uuid, uuid)
                TO vinx_runtime;

            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text);
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
                IF EXISTS (SELECT 1 FROM notifications.inbound_whatsapp_messages) THEN
                    RAISE EXCEPTION 'cannot downgrade inbound whatsapp media admission while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION notifications.resolve_inbound_whatsapp_actor(text)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION notifications.admit_inbound_whatsapp_media(uuid, text, text, text, text, text, text, text, uuid, uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION notifications.resolve_inbound_whatsapp_actor(text);
            DROP FUNCTION notifications.admit_inbound_whatsapp_media(uuid, text, text, text, text, text, text, text, uuid, uuid, uuid);
            ALTER TABLE notifications.inbound_whatsapp_messages
                DROP CONSTRAINT uq_inbound_whatsapp_messages_channel_message,
                DROP CONSTRAINT ck_inbound_whatsapp_messages_admitted_identity,
                DROP COLUMN phone_number_id,
                DROP COLUMN message_type,
                DROP COLUMN resolution_state,
                DROP COLUMN resolved_user_id,
                DROP COLUMN resolved_company_id,
                DROP COLUMN resolved_membership_id,
                DROP COLUMN resolved_at;
            ALTER TABLE notifications.inbound_whatsapp_messages
                ADD CONSTRAINT uq_inbound_whatsapp_messages_message_id UNIQUE (message_id);

            CREATE FUNCTION notifications.record_inbound_whatsapp_message(
                p_inbound_id uuid,
                p_message_id text,
                p_wa_id text,
                p_media_id text,
                p_caption text)
            RETURNS text
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, notifications, pg_temp
            AS $function$
            BEGIN
                IF p_inbound_id IS NULL
                    OR p_message_id IS NULL OR btrim(p_message_id) = '' OR char_length(p_message_id) > 200
                    OR p_wa_id IS NULL OR p_wa_id !~ '^[1-9][0-9]{6,14}$'
                    OR p_media_id IS NULL OR btrim(p_media_id) = '' OR char_length(p_media_id) > 200
                    OR (p_caption IS NOT NULL AND (btrim(p_caption) = '' OR char_length(p_caption) > 1000)) THEN
                    RAISE EXCEPTION 'inbound whatsapp message parameters are invalid' USING ERRCODE = '22023';
                END IF;

                INSERT INTO notifications.inbound_whatsapp_messages (
                    id, message_id, wa_id, media_id, caption, status)
            VALUES (
                    p_inbound_id, p_message_id, p_wa_id, p_media_id, p_caption, 'pending')
                ON CONFLICT (message_id) DO NOTHING;
                RETURN CASE WHEN FOUND THEN 'created' ELSE 'replay' END;
            END;
            $function$;
            REVOKE ALL ON FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)
                TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA notifications FROM vinx_notifications_owner;
            DROP POLICY memberships_notifications_owner_read ON foundation.memberships;
            DROP POLICY companies_notifications_owner_read ON foundation.companies;
            REVOKE SELECT (id, company_id, user_id, deleted_at) ON foundation.memberships
                FROM vinx_notifications_owner;
            REVOKE SELECT (id, deleted_at) ON foundation.companies
                FROM vinx_notifications_owner;
            """);
}
