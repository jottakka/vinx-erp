using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformOperatorBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_platform_owner;
                GRANT USAGE ON SCHEMA audit TO vinx_platform_owner;
                GRANT SELECT (id, email), INSERT (id, display_name, email, created_at)
                    ON TABLE foundation.users TO vinx_platform_owner;
                GRANT SELECT (id, user_id, issuer, subject), INSERT (id, user_id, issuer, subject, created_at),
                    REFERENCES (id) ON TABLE foundation.external_identities TO vinx_platform_owner;

                SET ROLE vinx_audit_owner;
                ALTER FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    SET search_path TO pg_catalog, audit, pg_temp;
                GRANT EXECUTE ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    TO vinx_platform_owner;
                RESET ROLE;

                SET ROLE vinx_platform_owner;

                CREATE TABLE foundation.platform_operators (
                    singleton smallint NOT NULL,
                    external_identity_id uuid NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                    CONSTRAINT pk_platform_operators PRIMARY KEY (singleton),
                    CONSTRAINT ck_platform_operators_singleton CHECK (singleton = 1),
                    CONSTRAINT uq_platform_operators_external_identity_id UNIQUE (external_identity_id),
                    CONSTRAINT fk_platform_operators_external_identities_external_identity_id
                        FOREIGN KEY (external_identity_id)
                        REFERENCES foundation.external_identities(id) ON DELETE RESTRICT
                );

                ALTER TABLE foundation.platform_operators ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.platform_operators FORCE ROW LEVEL SECURITY;
                CREATE POLICY platform_operators_owner_access
                    ON foundation.platform_operators
                    FOR ALL
                    TO vinx_platform_owner
                    USING (true)
                    WITH CHECK (true);

                CREATE FUNCTION foundation.current_actor_is_platform_operator()
                RETURNS boolean
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path TO pg_catalog, foundation, pg_temp
                AS $function$
                    SELECT EXISTS (
                        SELECT 1
                        FROM foundation.platform_operators AS platform_operator
                        WHERE platform_operator.external_identity_id =
                            NULLIF(current_setting('app.external_identity_id', true), '')::uuid);
                $function$;

                CREATE FUNCTION foundation.bootstrap_platform_operator(
                    p_subject text,
                    p_issuer text,
                    p_display_name text,
                    p_provider_email text,
                    p_correlation_id uuid)
                RETURNS TABLE (status text, user_id uuid)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO pg_catalog, foundation, pg_temp
                AS $function$
                DECLARE
                    v_deployment_actor_id constant uuid := '00000000-0000-0000-0000-000000000001';
                    v_operator_user_id uuid;
                    v_operator_identity_id uuid;
                    v_identity_user_id uuid;
                    v_identity_id uuid;
                    v_email_user_id uuid;
                BEGIN
                    IF p_subject IS NULL
                        OR btrim(p_subject) = ''
                        OR char_length(p_subject) > 400
                        OR p_issuer IS NULL
                        OR btrim(p_issuer) = ''
                        OR char_length(p_issuer) > 200
                        OR p_display_name IS NULL
                        OR btrim(p_display_name) = ''
                        OR char_length(p_display_name) > 200
                        OR p_provider_email IS NULL
                        OR btrim(p_provider_email) = ''
                        OR char_length(p_provider_email) > 320
                        OR p_correlation_id IS NULL THEN
                        RAISE EXCEPTION 'platform operator bootstrap parameters are invalid'
                            USING ERRCODE = '22023';
                    END IF;

                    LOCK TABLE foundation.platform_operators IN SHARE ROW EXCLUSIVE MODE;

                    SELECT platform_operator.external_identity_id, identity_record.user_id
                    INTO v_operator_identity_id, v_operator_user_id
                    FROM foundation.platform_operators AS platform_operator
                    INNER JOIN foundation.external_identities AS identity_record
                        ON identity_record.id = platform_operator.external_identity_id
                    WHERE platform_operator.singleton = 1;

                    IF FOUND THEN
                        SELECT identity_record.id, identity_record.user_id
                        INTO v_identity_id, v_identity_user_id
                        FROM foundation.external_identities AS identity_record
                        WHERE identity_record.issuer = p_issuer
                          AND identity_record.subject = p_subject;

                        IF FOUND AND v_identity_id = v_operator_identity_id THEN
                            RETURN QUERY SELECT 'already_configured'::text, v_operator_user_id;
                            RETURN;
                        END IF;

                        PERFORM audit.append_event(
                            v_deployment_actor_id,
                            'global',
                            NULL,
                            'worker',
                            'platform.operator.bootstrap',
                            'platform_operator',
                            NULL,
                            'denied',
                            p_correlation_id,
                            'deployment_bootstrap_conflict',
                            '{}'::jsonb);
                        RETURN QUERY SELECT 'conflict'::text, NULL::uuid;
                        RETURN;
                    END IF;

                    SELECT identity_record.id, identity_record.user_id
                    INTO v_identity_id, v_identity_user_id
                    FROM foundation.external_identities AS identity_record
                    WHERE identity_record.issuer = p_issuer
                      AND identity_record.subject = p_subject;

                    IF FOUND THEN
                        v_operator_user_id := v_identity_user_id;
                    ELSE
                        SELECT user_record.id
                        INTO v_email_user_id
                        FROM foundation.users AS user_record
                        WHERE lower(user_record.email) = lower(p_provider_email);

                        IF FOUND THEN
                            PERFORM audit.append_event(
                                v_deployment_actor_id,
                                'global',
                                NULL,
                                'worker',
                                'platform.operator.bootstrap',
                                'platform_operator',
                                NULL,
                                'denied',
                                p_correlation_id,
                                'deployment_bootstrap_conflict',
                                '{}'::jsonb);
                            RETURN QUERY SELECT 'conflict'::text, NULL::uuid;
                            RETURN;
                        END IF;

                        v_operator_user_id := gen_random_uuid();
                        v_identity_id := gen_random_uuid();
                        INSERT INTO foundation.users (id, display_name, email, created_at)
                        VALUES (
                            v_operator_user_id,
                            btrim(p_display_name),
                            btrim(p_provider_email),
                            clock_timestamp());
                        INSERT INTO foundation.external_identities (
                            id,
                            user_id,
                            issuer,
                            subject,
                            created_at)
                        VALUES (
                            v_identity_id,
                            v_operator_user_id,
                            p_issuer,
                            p_subject,
                            clock_timestamp());
                    END IF;

                    INSERT INTO foundation.platform_operators (singleton, external_identity_id)
                    VALUES (1, v_identity_id);

                    PERFORM audit.append_event(
                        v_deployment_actor_id,
                        'global',
                        NULL,
                        'worker',
                        'platform.operator.bootstrap',
                        'user',
                        v_operator_user_id,
                        'success',
                        p_correlation_id,
                        'deployment_bootstrap',
                        '{}'::jsonb);
                    RETURN QUERY SELECT 'created'::text, v_operator_user_id;
                END;
                $function$;

                REVOKE ALL ON FUNCTION foundation.current_actor_is_platform_operator()
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                GRANT EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator()
                    TO vinx_runtime;
                REVOKE ALL ON FUNCTION foundation.bootstrap_platform_operator(
                    text, text, text, text, uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                GRANT EXECUTE ON FUNCTION foundation.bootstrap_platform_operator(
                    text, text, text, text, uuid)
                    TO vinx_migrator;

                RESET ROLE;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_platform_owner;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ROLE vinx_platform_owner;

                LOCK TABLE foundation.platform_operators IN ACCESS EXCLUSIVE MODE;
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM foundation.platform_operators) THEN
                        RAISE EXCEPTION 'cannot downgrade platform operator bootstrap while an operator exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                REVOKE ALL ON FUNCTION foundation.current_actor_is_platform_operator()
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                REVOKE ALL ON FUNCTION foundation.bootstrap_platform_operator(
                    text, text, text, text, uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                DROP FUNCTION foundation.bootstrap_platform_operator(text, text, text, text, uuid);
                DROP FUNCTION foundation.current_actor_is_platform_operator();
                DROP TABLE foundation.platform_operators;

                RESET ROLE;

                SET ROLE vinx_audit_owner;
                REVOKE EXECUTE ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    FROM vinx_platform_owner;
                RESET ROLE;

                REVOKE INSERT (id, user_id, issuer, subject, created_at),
                    SELECT (id, user_id, issuer, subject), REFERENCES (id)
                    ON TABLE foundation.external_identities FROM vinx_platform_owner;
                REVOKE INSERT (id, display_name, email, created_at),
                    SELECT (id, email)
                    ON TABLE foundation.users FROM vinx_platform_owner;
                REVOKE USAGE ON SCHEMA audit FROM vinx_platform_owner;
                REVOKE USAGE ON SCHEMA foundation FROM vinx_platform_owner;
                """
            );
        }
    }
}
