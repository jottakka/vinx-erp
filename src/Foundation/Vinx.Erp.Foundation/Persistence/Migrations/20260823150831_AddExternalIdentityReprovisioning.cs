using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdentityReprovisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_identity_owner;
                GRANT USAGE ON SCHEMA audit TO vinx_identity_owner;
                GRANT SELECT (id, user_id, issuer, subject), UPDATE (subject)
                    ON TABLE foundation.external_identities TO vinx_identity_owner;
                GRANT SELECT (revocation_reason)
                    ON TABLE foundation.user_sessions TO vinx_identity_owner;

                SET ROLE vinx_audit_owner;
                GRANT EXECUTE ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    TO vinx_identity_owner;
                RESET ROLE;

                SET ROLE vinx_identity_owner;
                CREATE FUNCTION foundation.reprovision_external_identity(
                    p_user_id uuid,
                    p_expected_identity_id uuid,
                    p_expected_issuer text,
                    p_expected_old_subject text,
                    p_new_subject text,
                    p_correlation_id uuid)
                RETURNS TABLE (status text)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO pg_catalog, foundation, pg_temp
                AS $function$
                DECLARE
                    v_identity_user_id uuid;
                    v_identity_issuer text;
                    v_identity_subject text;
                    v_existing_new_identity_id uuid;
                    v_now timestamp with time zone;
                BEGIN
                    IF p_user_id IS NULL
                        OR p_expected_identity_id IS NULL
                        OR p_expected_issuer IS NULL
                        OR btrim(p_expected_issuer) = ''
                        OR char_length(p_expected_issuer) > 200
                        OR p_expected_old_subject IS NULL
                        OR btrim(p_expected_old_subject) = ''
                        OR char_length(p_expected_old_subject) > 400
                        OR p_new_subject IS NULL
                        OR btrim(p_new_subject) = ''
                        OR char_length(p_new_subject) > 400
                        OR p_correlation_id IS NULL THEN
                        RETURN QUERY SELECT 'invalid'::text;
                        RETURN;
                    END IF;

                    PERFORM 1 FROM foundation.users AS user_record
                    WHERE user_record.id = p_user_id FOR UPDATE;
                    IF NOT FOUND THEN
                        RETURN QUERY SELECT 'invalid'::text;
                        RETURN;
                    END IF;

                    SELECT identity_record.user_id, identity_record.issuer, identity_record.subject
                    INTO v_identity_user_id, v_identity_issuer, v_identity_subject
                    FROM foundation.external_identities AS identity_record
                    WHERE identity_record.id = p_expected_identity_id FOR UPDATE;
                    IF NOT FOUND THEN
                        RETURN QUERY SELECT 'invalid'::text;
                        RETURN;
                    END IF;

                    IF v_identity_user_id = p_user_id
                        AND v_identity_issuer = p_expected_issuer
                        AND v_identity_subject = p_new_subject THEN
                        RETURN QUERY SELECT 'already_configured'::text;
                        RETURN;
                    END IF;

                    IF v_identity_user_id <> p_user_id
                        OR v_identity_issuer <> p_expected_issuer
                        OR v_identity_subject <> p_expected_old_subject THEN
                        RETURN QUERY SELECT 'conflict'::text;
                        RETURN;
                    END IF;

                    SELECT identity_record.id INTO v_existing_new_identity_id
                    FROM foundation.external_identities AS identity_record
                    WHERE identity_record.issuer = p_expected_issuer
                      AND identity_record.subject = p_new_subject FOR UPDATE;
                    IF FOUND AND v_existing_new_identity_id <> p_expected_identity_id THEN
                        RETURN QUERY SELECT 'conflict'::text;
                        RETURN;
                    END IF;

                    v_now := pg_catalog.clock_timestamp();
                    UPDATE foundation.external_identities SET subject = p_new_subject
                    WHERE id = p_expected_identity_id;
                    UPDATE foundation.users
                    SET sessions_valid_after = CASE
                        WHEN sessions_valid_after IS NULL OR sessions_valid_after < v_now THEN v_now
                        ELSE sessions_valid_after
                    END
                    WHERE id = p_user_id;
                    UPDATE foundation.user_sessions
                    SET revoked_at = v_now,
                        revoked_by_user_id = p_user_id,
                        revocation_reason = 'identity_reprovisioned'
                    WHERE user_id = p_user_id AND revoked_at IS NULL;

                    PERFORM audit.append_event(
                        p_user_id, 'global', NULL, 'worker', 'identity.external.reprovision',
                        'external_identity', p_expected_identity_id, 'success', p_correlation_id,
                        'same_person_recovery', '{}'::jsonb);
                    RETURN QUERY SELECT 'reprovisioned'::text;
                END;
                $function$;

                REVOKE ALL ON FUNCTION foundation.reprovision_external_identity(
                    uuid, uuid, text, text, text, uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                GRANT EXECUTE ON FUNCTION foundation.reprovision_external_identity(
                    uuid, uuid, text, text, text, uuid)
                    TO vinx_migrator;
                RESET ROLE;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_identity_owner;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE foundation.external_identities IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE foundation.users IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE foundation.user_sessions IN ACCESS EXCLUSIVE MODE;

                SET ROLE vinx_audit_owner;
                LOCK TABLE audit.audit_events IN ACCESS EXCLUSIVE MODE;
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM audit.audit_events
                        WHERE action = 'identity.external.reprovision') THEN
                        RAISE EXCEPTION 'cannot downgrade external identity reprovisioning while recovery audit state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;
                RESET ROLE;

                SET ROLE vinx_identity_owner;
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM foundation.user_sessions
                        WHERE revocation_reason = 'identity_reprovisioned') THEN
                        RAISE EXCEPTION 'cannot downgrade external identity reprovisioning while session recovery state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                REVOKE ALL ON FUNCTION foundation.reprovision_external_identity(
                    uuid, uuid, text, text, text, uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                DROP FUNCTION foundation.reprovision_external_identity(uuid, uuid, text, text, text, uuid);
                RESET ROLE;

                SET ROLE vinx_audit_owner;
                REVOKE EXECUTE ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    FROM vinx_identity_owner;
                RESET ROLE;
                REVOKE UPDATE (subject), SELECT (id, user_id, issuer, subject)
                    ON TABLE foundation.external_identities FROM vinx_identity_owner;
                REVOKE SELECT (revocation_reason)
                    ON TABLE foundation.user_sessions FROM vinx_identity_owner;
                REVOKE USAGE ON SCHEMA audit FROM vinx_identity_owner;
                """
            );
        }
    }
}
