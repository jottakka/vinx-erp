using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionGateAndSelfRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_identity_owner;
                GRANT SELECT (user_id, revoked_at) ON TABLE foundation.user_sessions
                    TO vinx_identity_owner;
                GRANT UPDATE (revoked_at, revoked_by_user_id, revocation_reason)
                    ON TABLE foundation.user_sessions TO vinx_identity_owner;

                CREATE FUNCTION foundation.revoke_all_user_sessions(p_user_id uuid)
                RETURNS integer
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO foundation, pg_catalog
                AS $function$
                DECLARE
                    v_now timestamp with time zone;
                    v_revoked_session_count integer;
                BEGIN
                    PERFORM 1
                    FROM foundation.users AS user_record
                    WHERE user_record.id = p_user_id
                    FOR UPDATE;

                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'internal user does not exist'
                            USING ERRCODE = 'P0002';
                    END IF;

                    v_now := clock_timestamp();

                    WITH revoked_sessions AS (
                        UPDATE foundation.user_sessions
                        SET revoked_at = v_now,
                            revoked_by_user_id = p_user_id,
                            revocation_reason = 'self_revoke_all'
                        WHERE user_id = p_user_id
                          AND revoked_at IS NULL
                        RETURNING 1)
                    SELECT count(*)::integer
                    INTO v_revoked_session_count
                    FROM revoked_sessions;

                    IF v_revoked_session_count > 0 THEN
                        UPDATE foundation.users
                        SET sessions_valid_after = CASE
                            WHEN sessions_valid_after IS NULL OR sessions_valid_after < v_now THEN v_now
                            ELSE sessions_valid_after
                        END
                        WHERE id = p_user_id;
                    END IF;

                    RETURN v_revoked_session_count;
                END;
                $function$;

                REVOKE ALL ON FUNCTION foundation.revoke_all_user_sessions(uuid)
                    FROM PUBLIC, vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.revoke_all_user_sessions(uuid)
                    TO vinx_runtime;
                ALTER FUNCTION foundation.revoke_all_user_sessions(uuid)
                    OWNER TO vinx_identity_owner;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_identity_owner;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE foundation.user_sessions IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE foundation.users IN ACCESS EXCLUSIVE MODE;
                SET ROLE vinx_audit_owner;
                LOCK TABLE audit.audit_events IN ACCESS EXCLUSIVE MODE;

                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM audit.audit_events
                        WHERE action = 'identity_sessions_revoked') THEN
                        RAISE EXCEPTION 'cannot downgrade self session revocation while audit state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                RESET ROLE;
                SET ROLE vinx_identity_owner;
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM foundation.users
                        WHERE sessions_valid_after IS NOT NULL)
                        OR EXISTS (
                            SELECT 1
                            FROM foundation.user_sessions
                            WHERE revoked_at IS NOT NULL) THEN
                        RAISE EXCEPTION 'cannot downgrade self session revocation while session state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                REVOKE ALL ON FUNCTION foundation.revoke_all_user_sessions(uuid)
                    FROM PUBLIC, vinx_runtime;
                DROP FUNCTION foundation.revoke_all_user_sessions(uuid);
                RESET ROLE;
                REVOKE UPDATE (revoked_at, revoked_by_user_id, revocation_reason)
                    ON TABLE foundation.user_sessions FROM vinx_identity_owner;
                REVOKE SELECT (user_id, revoked_at) ON TABLE foundation.user_sessions
                    FROM vinx_identity_owner;
                """);

        }
    }
}
