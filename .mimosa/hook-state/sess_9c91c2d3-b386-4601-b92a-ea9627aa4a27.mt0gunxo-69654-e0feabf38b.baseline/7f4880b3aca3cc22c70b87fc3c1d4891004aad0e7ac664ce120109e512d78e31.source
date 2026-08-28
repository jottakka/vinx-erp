using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

public partial class AddEmployeeDirectory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_employee_directory_owner;
            GRANT SELECT ON foundation.employees TO vinx_runtime;
            GRANT INSERT ON foundation.users, foundation.memberships, foundation.employees,
                foundation.idempotency_records, foundation.role_capabilities
                TO vinx_employee_directory_owner;
            GRANT SELECT ON foundation.memberships, foundation.roles, foundation.role_assignments,
                foundation.role_capabilities, foundation.capabilities, foundation.idempotency_records
                TO vinx_employee_directory_owner;
            GRANT REFERENCES (id) ON foundation.companies, foundation.users, foundation.memberships
                TO vinx_employee_directory_owner;
            GRANT USAGE ON SCHEMA audit TO vinx_employee_directory_owner;
            GRANT EXECUTE ON FUNCTION foundation.current_authorized_company_ids()
                TO vinx_employee_directory_owner;

            SET ROLE vinx_platform_owner;
            GRANT EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator()
                TO vinx_employee_directory_owner;
            RESET ROLE;

            SET ROLE vinx_audit_owner;
            GRANT EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                TO vinx_employee_directory_owner;
            RESET ROLE;

            CREATE POLICY memberships_employee_directory_owner_select
                ON foundation.memberships FOR SELECT TO vinx_employee_directory_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY memberships_employee_directory_owner_insert
                ON foundation.memberships FOR INSERT TO vinx_employee_directory_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY employees_employee_directory_owner_insert
                ON foundation.employees FOR INSERT TO vinx_employee_directory_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY roles_employee_directory_owner_select
                ON foundation.roles FOR SELECT TO vinx_employee_directory_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY role_assignments_employee_directory_owner_select
                ON foundation.role_assignments FOR SELECT TO vinx_employee_directory_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY role_capabilities_employee_directory_owner_select
                ON foundation.role_capabilities FOR SELECT TO vinx_employee_directory_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY role_capabilities_employee_directory_owner_insert
                ON foundation.role_capabilities FOR INSERT TO vinx_employee_directory_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY capabilities_employee_directory_owner_select
                ON foundation.capabilities FOR SELECT TO vinx_employee_directory_owner
                USING (name IN ('foundation.roles.assign', 'foundation.employees.create'));
            CREATE POLICY idempotency_employee_directory_owner_select
                ON foundation.idempotency_records FOR SELECT TO vinx_employee_directory_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY idempotency_employee_directory_owner_insert
                ON foundation.idempotency_records FOR INSERT TO vinx_employee_directory_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            SET ROLE vinx_provisioning_owner;
            GRANT SELECT ON foundation.organization_provisioning_receipts
                TO vinx_employee_directory_owner;
            CREATE POLICY organization_provisioning_receipts_employee_directory_owner_select
                ON foundation.organization_provisioning_receipts FOR SELECT TO vinx_employee_directory_owner
                USING (
                    actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                    AND company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            RESET ROLE;

            INSERT INTO foundation.capabilities (id, name, created_at)
            VALUES (
                'b81d5f86-6f68-5846-bbe7-61a244f9e212',
                'foundation.employees.create',
                clock_timestamp())
            ON CONFLICT (name) DO NOTHING;

            SET ROLE vinx_employee_directory_owner;
            CREATE FUNCTION foundation.create_employee_directory_entry(
                p_request_id uuid,
                p_company_id uuid,
                p_display_name text,
                p_email text,
                p_payload_hash text)
            RETURNS TABLE (status text, employee_id uuid, membership_id uuid, user_id uuid)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_correlation_id uuid := COALESCE(
                    NULLIF(current_setting('app.correlation_id', true), '')::uuid,
                    gen_random_uuid());
                v_existing foundation.idempotency_records%ROWTYPE;
                v_user_id uuid := gen_random_uuid();
                v_membership_id uuid := gen_random_uuid();
                v_employee_id uuid := gen_random_uuid();
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF p_company_id IS NULL
                    OR p_request_id IS NULL
                    OR p_payload_hash !~ '^[0-9a-f]{64}$'
                    OR p_display_name IS NULL
                    OR btrim(p_display_name) = ''
                    OR char_length(btrim(p_display_name)) > 200
                    OR p_email IS NULL
                    OR btrim(p_email) = ''
                    OR char_length(btrim(p_email)) > 320 THEN
                    RAISE EXCEPTION 'employee directory parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

                IF v_actor_user_id IS NULL
                    OR NOT EXISTS (
                        SELECT 1
                        FROM foundation.memberships AS membership
                        INNER JOIN foundation.role_assignments AS assignment
                            ON assignment.company_id = membership.company_id
                           AND assignment.membership_id = membership.id
                           AND assignment.deleted_at IS NULL
                        INNER JOIN foundation.roles AS role_record
                            ON role_record.company_id = assignment.company_id
                           AND role_record.id = assignment.role_id
                           AND role_record.deleted_at IS NULL
                        INNER JOIN foundation.role_capabilities AS grant_record
                            ON grant_record.company_id = assignment.company_id
                           AND grant_record.role_id = assignment.role_id
                           AND grant_record.scope = 'company'
                           AND grant_record.deleted_at IS NULL
                        INNER JOIN foundation.capabilities AS capability
                            ON capability.id = grant_record.capability_id
                           AND capability.name = 'foundation.employees.create'
                        WHERE membership.company_id = p_company_id
                          AND membership.user_id = v_actor_user_id
                          AND membership.deleted_at IS NULL) THEN
                    RAISE EXCEPTION 'employee directory capability is required'
                        USING ERRCODE = '42501';
                END IF;

                PERFORM pg_advisory_xact_lock(
                    hashtextextended(
                        v_actor_user_id::text || '|' || p_company_id::text || '|' || p_request_id::text,
                        0));
                SELECT * INTO v_existing
                FROM foundation.idempotency_records
                WHERE company_id = p_company_id
                  AND actor_user_id = v_actor_user_id
                  AND command_name = 'foundation.employee.create'
                  AND request_id = p_request_id;
                IF FOUND THEN
                    RETURN QUERY
                    SELECT
                        CASE
                            WHEN v_existing.payload_hash = p_payload_hash THEN 'replay'
                            ELSE 'idempotency_conflict'
                        END,
                        (v_existing.result_json ->> 'employeeId')::uuid,
                        (v_existing.result_json ->> 'membershipId')::uuid,
                        (v_existing.result_json ->> 'userId')::uuid;
                    RETURN;
                END IF;

                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (v_user_id, btrim(p_display_name), lower(btrim(p_email)), v_now);
                INSERT INTO foundation.memberships (
                    id, company_id, user_id, created_by_user_id, updated_by_user_id,
                    request_id, created_at, updated_at)
                VALUES (
                    v_membership_id, p_company_id, v_user_id, v_actor_user_id, v_actor_user_id,
                    p_request_id, v_now, v_now);
                INSERT INTO foundation.employees (
                    id, company_id, membership_id, display_name, created_by_user_id,
                    updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    v_employee_id, p_company_id, v_membership_id, btrim(p_display_name),
                    v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
                INSERT INTO foundation.idempotency_records (
                    id, company_id, actor_user_id, command_name, request_id,
                    payload_hash, result_json, created_at)
                VALUES (
                    gen_random_uuid(), p_company_id, v_actor_user_id, 'foundation.employee.create',
                    p_request_id, p_payload_hash,
                    jsonb_build_object(
                        'employeeId', v_employee_id,
                        'membershipId', v_membership_id,
                        'userId', v_user_id),
                    v_now);
                PERFORM audit.append_event(
                    v_actor_user_id,
                    'company',
                    p_company_id,
                    'web',
                    'foundation.employee.create',
                    'employee',
                    v_employee_id,
                    'success',
                    v_correlation_id,
                    NULL,
                    '{}'::jsonb);
                RETURN QUERY SELECT 'created'::text, v_employee_id, v_membership_id, v_user_id;
            END;
            $function$;

            CREATE FUNCTION foundation.ensure_platform_owner_employee_create(
                p_request_id uuid,
                p_company_id uuid,
                p_owner_membership_id uuid)
            RETURNS void
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_has_roles_assign boolean;
            BEGIN
                IF p_request_id IS NULL OR p_company_id IS NULL OR p_owner_membership_id IS NULL THEN
                    RAISE EXCEPTION 'initial owner employee capability parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

                IF v_actor_user_id IS NULL
                    OR NOT foundation.current_actor_is_platform_operator() THEN
                    RAISE EXCEPTION 'platform operator is required' USING ERRCODE = '42501';
                END IF;
                IF current_setting('app.mfa_assurance', true) IS DISTINCT FROM 'aal2' THEN
                    RAISE EXCEPTION 'mfa assurance is required' USING ERRCODE = '42501';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.organization_provisioning_receipts AS receipt
                    WHERE receipt.actor_user_id = v_actor_user_id
                      AND receipt.request_id = p_request_id
                      AND receipt.company_id = p_company_id
                      AND receipt.owner_membership_id = p_owner_membership_id) THEN
                    RAISE EXCEPTION 'initial owner provisioning receipt is required'
                        USING ERRCODE = '42501';
                END IF;

                SELECT EXISTS (
                    SELECT 1
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.role_assignments AS assignment
                        ON assignment.company_id = membership.company_id
                       AND assignment.membership_id = membership.id
                       AND assignment.deleted_at IS NULL
                    INNER JOIN foundation.roles AS role_record
                        ON role_record.company_id = assignment.company_id
                       AND role_record.id = assignment.role_id
                       AND role_record.deleted_at IS NULL
                    INNER JOIN foundation.role_capabilities AS grant_record
                        ON grant_record.company_id = assignment.company_id
                       AND grant_record.role_id = assignment.role_id
                       AND grant_record.scope = 'company'
                       AND grant_record.deleted_at IS NULL
                    INNER JOIN foundation.capabilities AS capability
                        ON capability.id = grant_record.capability_id
                       AND capability.name = 'foundation.roles.assign'
                    WHERE membership.company_id = p_company_id
                      AND membership.id = p_owner_membership_id
                      AND membership.deleted_at IS NULL)
                INTO v_has_roles_assign;
                IF NOT v_has_roles_assign THEN
                    RAISE EXCEPTION 'initial owner employee capability is unavailable'
                        USING ERRCODE = '23514';
                END IF;

                INSERT INTO foundation.role_capabilities (
                    id, company_id, role_id, capability_id, scope,
                    created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                SELECT
                    gen_random_uuid(),
                    assignment.company_id,
                    assignment.role_id,
                    employee_capability.id,
                    'company',
                    v_actor_user_id,
                    v_actor_user_id,
                    gen_random_uuid(),
                    clock_timestamp(),
                    clock_timestamp()
                FROM foundation.role_assignments AS assignment
                INNER JOIN foundation.role_capabilities AS roles_assign
                    ON roles_assign.company_id = assignment.company_id
                   AND roles_assign.role_id = assignment.role_id
                   AND roles_assign.scope = 'company'
                   AND roles_assign.deleted_at IS NULL
                INNER JOIN foundation.capabilities AS assigned_capability
                    ON assigned_capability.id = roles_assign.capability_id
                   AND assigned_capability.name = 'foundation.roles.assign'
                INNER JOIN foundation.capabilities AS employee_capability
                    ON employee_capability.name = 'foundation.employees.create'
                WHERE assignment.company_id = p_company_id
                  AND assignment.membership_id = p_owner_membership_id
                  AND assignment.deleted_at IS NULL
                ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                    DO NOTHING;
            END;
            $function$;

            REVOKE ALL ON FUNCTION foundation.create_employee_directory_entry(
                uuid, uuid, text, text, text) FROM PUBLIC, vinx_migrator;
            REVOKE ALL ON FUNCTION foundation.ensure_platform_owner_employee_create(
                uuid, uuid, uuid) FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.create_employee_directory_entry(
                uuid, uuid, text, text, text) TO vinx_runtime;
            GRANT EXECUTE ON FUNCTION foundation.ensure_platform_owner_employee_create(
                uuid, uuid, uuid) TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_employee_directory_owner;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE POLICY idempotency_employee_directory_owner_maintenance_select
                ON foundation.idempotency_records FOR SELECT TO vinx_employee_directory_owner
                USING (true);
            CREATE POLICY role_capabilities_employee_directory_owner_maintenance_select
                ON foundation.role_capabilities FOR SELECT TO vinx_employee_directory_owner
                USING (EXISTS (
                    SELECT 1
                    FROM foundation.capabilities AS capability
                    WHERE capability.id = capability_id
                      AND capability.name = 'foundation.employees.create'));
            SET ROLE vinx_employee_directory_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM foundation.idempotency_records
                    WHERE command_name = 'foundation.employee.create')
                    OR EXISTS (
                        SELECT 1
                        FROM foundation.role_capabilities AS grant_record
                        INNER JOIN foundation.capabilities AS capability
                            ON capability.id = grant_record.capability_id
                        WHERE capability.name = 'foundation.employees.create'
                          AND grant_record.deleted_at IS NULL) THEN
                    RAISE EXCEPTION 'cannot downgrade employee directory while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            RESET ROLE;
            DROP POLICY idempotency_employee_directory_owner_maintenance_select
                ON foundation.idempotency_records;
            DROP POLICY role_capabilities_employee_directory_owner_maintenance_select
                ON foundation.role_capabilities;

            SET ROLE vinx_employee_directory_owner;
            REVOKE ALL ON FUNCTION foundation.ensure_platform_owner_employee_create(uuid, uuid, uuid)
                FROM PUBLIC, vinx_runtime, vinx_migrator;
            REVOKE ALL ON FUNCTION foundation.create_employee_directory_entry(
                uuid, uuid, text, text, text) FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION foundation.ensure_platform_owner_employee_create(uuid, uuid, uuid);
            DROP FUNCTION foundation.create_employee_directory_entry(uuid, uuid, text, text, text);
            RESET ROLE;

            DELETE FROM foundation.capabilities
            WHERE id = 'b81d5f86-6f68-5846-bbe7-61a244f9e212'::uuid;

            DROP POLICY idempotency_employee_directory_owner_insert ON foundation.idempotency_records;
            DROP POLICY idempotency_employee_directory_owner_select ON foundation.idempotency_records;
            SET ROLE vinx_provisioning_owner;
            DROP POLICY organization_provisioning_receipts_employee_directory_owner_select
                ON foundation.organization_provisioning_receipts;
            REVOKE SELECT ON foundation.organization_provisioning_receipts
                FROM vinx_employee_directory_owner;
            RESET ROLE;
            DROP POLICY capabilities_employee_directory_owner_select ON foundation.capabilities;
            DROP POLICY role_capabilities_employee_directory_owner_insert ON foundation.role_capabilities;
            DROP POLICY role_capabilities_employee_directory_owner_select ON foundation.role_capabilities;
            DROP POLICY role_assignments_employee_directory_owner_select ON foundation.role_assignments;
            DROP POLICY roles_employee_directory_owner_select ON foundation.roles;
            DROP POLICY employees_employee_directory_owner_insert ON foundation.employees;
            DROP POLICY memberships_employee_directory_owner_insert ON foundation.memberships;
            DROP POLICY memberships_employee_directory_owner_select ON foundation.memberships;

            SET ROLE vinx_audit_owner;
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_employee_directory_owner;
            RESET ROLE;

            SET ROLE vinx_platform_owner;
            REVOKE EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator()
                FROM vinx_employee_directory_owner;
            RESET ROLE;

            REVOKE SELECT ON foundation.employees FROM vinx_runtime;
            REVOKE EXECUTE ON FUNCTION foundation.current_authorized_company_ids()
                FROM vinx_employee_directory_owner;
            REVOKE REFERENCES (id) ON foundation.companies, foundation.users, foundation.memberships
                FROM vinx_employee_directory_owner;
            REVOKE INSERT ON foundation.users, foundation.memberships, foundation.employees,
                foundation.idempotency_records, foundation.role_capabilities
                FROM vinx_employee_directory_owner;
            REVOKE SELECT ON foundation.memberships, foundation.roles, foundation.role_assignments,
                foundation.role_capabilities, foundation.capabilities, foundation.idempotency_records
                FROM vinx_employee_directory_owner;
            REVOKE USAGE ON SCHEMA audit, foundation FROM vinx_employee_directory_owner;
            """
        );
}
