using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA audit TO vinx_audit_owner;
                SET ROLE vinx_audit_owner;

                ALTER TABLE audit.audit_events
                    ADD COLUMN scope character varying(20) NOT NULL DEFAULT 'company';
                ALTER TABLE audit.audit_events
                    DROP CONSTRAINT ck_audit_events_requested_company;
                ALTER TABLE audit.audit_events
                    DISABLE TRIGGER audit_events_reject_mutation;
                UPDATE audit.audit_events
                    SET scope = 'global'
                    WHERE requested_company_id IS NULL;
                ALTER TABLE audit.audit_events
                    ENABLE TRIGGER audit_events_reject_mutation;
                ALTER TABLE audit.audit_events
                    ADD CONSTRAINT ck_audit_events_requested_company CHECK (
                        (scope = 'global' AND requested_company_id IS NULL)
                        OR (scope = 'company' AND requested_company_id IS NOT NULL));
                ALTER TABLE audit.audit_events ALTER COLUMN scope DROP DEFAULT;

                REVOKE ALL ON FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    FROM PUBLIC, vinx_runtime;
                DROP FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb);

                CREATE FUNCTION audit.append_event(
                    p_actor_user_id uuid,
                    p_scope text,
                    p_requested_company_id uuid,
                    p_source text,
                    p_action text,
                    p_resource_type text,
                    p_resource_id uuid,
                    p_outcome text,
                    p_correlation_id uuid,
                    p_reason text,
                    p_metadata jsonb)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO audit, pg_catalog
                AS $function$
                BEGIN
                    IF p_scope IS NULL OR p_scope NOT IN ('global', 'company') THEN
                        RAISE EXCEPTION 'audit scope is invalid'
                            USING ERRCODE = '23514';
                    END IF;

                    IF (p_scope = 'global' AND p_requested_company_id IS NOT NULL)
                        OR (p_scope = 'company' AND p_requested_company_id IS NULL) THEN
                        RAISE EXCEPTION 'audit scope and requested company are incompatible'
                            USING ERRCODE = '23514';
                    END IF;

                    INSERT INTO audit_events (
                        id, actor_user_id, scope, requested_company_id, source, action,
                        resource_type, resource_id, outcome, correlation_id, reason,
                        metadata_json, occurred_at)
                    VALUES (
                        gen_random_uuid(), p_actor_user_id, p_scope, p_requested_company_id,
                        p_source, p_action, p_resource_type, p_resource_id, p_outcome,
                        p_correlation_id, p_reason, p_metadata, clock_timestamp());
                END;
                $function$;

                REVOKE ALL ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    TO vinx_runtime;
                ALTER FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    OWNER TO vinx_audit_owner;

                RESET ROLE;
                REVOKE CREATE ON SCHEMA audit FROM vinx_audit_owner;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA audit TO vinx_audit_owner;
                SET ROLE vinx_audit_owner;

                LOCK TABLE audit.audit_events IN ACCESS EXCLUSIVE MODE;

                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM audit.audit_events WHERE scope = 'global') THEN
                        RAISE EXCEPTION 'cannot downgrade audit scope while global audit events exist'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                REVOKE ALL ON FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    FROM PUBLIC, vinx_runtime;
                DROP FUNCTION audit.append_event(
                    uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb);

                ALTER TABLE audit.audit_events
                    DROP CONSTRAINT ck_audit_events_requested_company;
                ALTER TABLE audit.audit_events
                    ADD CONSTRAINT ck_audit_events_requested_company CHECK (
                        requested_company_id IS NOT NULL
                        OR (action = 'company_context_required' AND outcome = 'denied'));
                ALTER TABLE audit.audit_events DROP COLUMN scope;

                CREATE FUNCTION audit.append_event(
                    p_actor_user_id uuid,
                    p_requested_company_id uuid,
                    p_source text,
                    p_action text,
                    p_resource_type text,
                    p_resource_id uuid,
                    p_outcome text,
                    p_correlation_id uuid,
                    p_reason text,
                    p_metadata jsonb)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO audit, pg_catalog
                AS $function$
                BEGIN
                    IF p_requested_company_id IS NULL
                        AND (p_action <> 'company_context_required' OR p_outcome <> 'denied') THEN
                        RAISE EXCEPTION 'requested company is required for this audit event'
                            USING ERRCODE = '23514';
                    END IF;

                    INSERT INTO audit_events (
                        id, actor_user_id, requested_company_id, source, action,
                        resource_type, resource_id, outcome, correlation_id, reason,
                        metadata_json, occurred_at)
                    VALUES (
                        gen_random_uuid(), p_actor_user_id, p_requested_company_id,
                        p_source, p_action, p_resource_type, p_resource_id, p_outcome,
                        p_correlation_id, p_reason, p_metadata, clock_timestamp());
                END;
                $function$;

                REVOKE ALL ON FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    TO vinx_runtime;
                ALTER FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                    OWNER TO vinx_audit_owner;

                RESET ROLE;
                REVOKE CREATE ON SCHEMA audit FROM vinx_audit_owner;
                """);
        }
    }
}
