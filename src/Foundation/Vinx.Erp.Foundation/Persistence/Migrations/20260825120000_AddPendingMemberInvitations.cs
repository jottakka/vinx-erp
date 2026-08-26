using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class AddPendingMemberInvitations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner;
            GRANT INSERT ON foundation.users, foundation.memberships, foundation.employees,
                foundation.idempotency_records TO vinx_permissions_owner;
            GRANT SELECT ON foundation.idempotency_records TO vinx_permissions_owner;
            GRANT REFERENCES (id) ON foundation.users, foundation.memberships
                TO vinx_permissions_owner;
            GRANT USAGE ON SCHEMA audit TO vinx_permissions_owner;

            SET ROLE vinx_audit_owner;
            GRANT EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                TO vinx_permissions_owner;
            RESET ROLE;

            CREATE POLICY memberships_permissions_owner_insert
                ON foundation.memberships FOR INSERT TO vinx_permissions_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY employees_permissions_owner_insert
                ON foundation.employees FOR INSERT TO vinx_permissions_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY idempotency_permissions_owner_select
                ON foundation.idempotency_records FOR SELECT TO vinx_permissions_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY idempotency_permissions_owner_insert
                ON foundation.idempotency_records FOR INSERT TO vinx_permissions_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

            SET ROLE vinx_permissions_owner;
            CREATE TABLE foundation.pending_member_invitations (
                id uuid PRIMARY KEY,
                membership_id uuid NOT NULL REFERENCES foundation.memberships(id) ON DELETE RESTRICT,
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                status varchar(30) NOT NULL CHECK (status = 'pending_delivery'),
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_pending_member_invitations_membership_id UNIQUE (membership_id)
            );
            ALTER TABLE foundation.pending_member_invitations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.pending_member_invitations FORCE ROW LEVEL SECURITY;
            CREATE POLICY pending_member_invitations_permissions_owner_select
                ON foundation.pending_member_invitations FOR SELECT TO vinx_permissions_owner
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            CREATE POLICY pending_member_invitations_permissions_owner_insert
                ON foundation.pending_member_invitations FOR INSERT TO vinx_permissions_owner
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

            CREATE FUNCTION foundation.create_pending_member_invitation(
                p_request_id uuid,
                p_company_id uuid,
                p_display_name text,
                p_email text,
                p_payload_hash text)
            RETURNS TABLE (status text, membership_id uuid, user_id uuid, employee_id uuid, invitation_id uuid)
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
                v_invitation_id uuid;
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
                    RAISE EXCEPTION 'member invitation parameters are invalid'
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
                           AND capability.name = 'foundation.roles.assign'
                        WHERE membership.company_id = p_company_id
                          AND membership.user_id = v_actor_user_id
                          AND membership.deleted_at IS NULL) THEN
                    RAISE EXCEPTION 'member invitation capability is required'
                        USING ERRCODE = 'PX425';
                END IF;

                PERFORM pg_advisory_xact_lock(
                    hashtextextended(
                        v_actor_user_id::text || '|' || p_company_id::text || '|' || p_request_id::text,
                        0));
                SELECT * INTO v_existing
                FROM foundation.idempotency_records
                WHERE company_id = p_company_id
                  AND actor_user_id = v_actor_user_id
                  AND command_name = 'foundation.member.invite'
                  AND request_id = p_request_id;
                IF FOUND THEN
                    RETURN QUERY
                    SELECT
                        CASE
                            WHEN v_existing.payload_hash = p_payload_hash THEN 'replay'
                            ELSE 'idempotency_conflict'
                        END,
                        (v_existing.result_json ->> 'membershipId')::uuid,
                        (v_existing.result_json ->> 'userId')::uuid,
                        (v_existing.result_json ->> 'employeeId')::uuid,
                        (v_existing.result_json ->> 'invitationId')::uuid;
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
                INSERT INTO foundation.pending_member_invitations (id, membership_id, user_id, company_id, status)
                VALUES (gen_random_uuid(), v_membership_id, v_user_id, p_company_id, 'pending_delivery')
                RETURNING id INTO v_invitation_id;
                INSERT INTO foundation.idempotency_records (
                    id, company_id, actor_user_id, command_name, request_id,
                    payload_hash, result_json, created_at)
                VALUES (
                    gen_random_uuid(), p_company_id, v_actor_user_id, 'foundation.member.invite',
                    p_request_id, p_payload_hash,
                    jsonb_build_object(
                        'membershipId', v_membership_id,
                        'userId', v_user_id,
                        'employeeId', v_employee_id,
                        'invitationId', v_invitation_id),
                    v_now);
                PERFORM audit.append_event(
                    v_actor_user_id,
                    'company',
                    p_company_id,
                    'web',
                    'foundation.member.invite',
                    'member_invitation',
                    v_invitation_id,
                    'success',
                    v_correlation_id,
                    NULL,
                    '{}'::jsonb);
                RETURN QUERY SELECT 'created'::text, v_membership_id, v_user_id, v_employee_id, v_invitation_id;
            END;
            $function$;

            CREATE OR REPLACE FUNCTION foundation.list_company_members_with_roles(p_company_id uuid)
            RETURNS TABLE (
                membership_id uuid,
                display_name text,
                role_ids uuid[],
                role_names text[],
                capability_names text[],
                invitation_status text)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
            BEGIN
                IF p_company_id IS NULL THEN
                    RAISE EXCEPTION 'member permissions parameters are invalid'
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
                           AND capability.name = 'foundation.roles.assign'
                        WHERE membership.company_id = p_company_id
                          AND membership.user_id = v_actor_user_id
                          AND membership.deleted_at IS NULL) THEN
                    RAISE EXCEPTION 'member permissions capability is required'
                        USING ERRCODE = 'PX425';
                END IF;

                RETURN QUERY
                WITH member_roles AS (
                    SELECT
                        assignment.membership_id AS member_id,
                        role_record.id AS granted_role_id,
                        role_record.name AS granted_role_name
                    FROM foundation.role_assignments AS assignment
                    INNER JOIN foundation.roles AS role_record
                        ON role_record.company_id = assignment.company_id
                       AND role_record.id = assignment.role_id
                       AND role_record.deleted_at IS NULL
                    WHERE assignment.company_id = p_company_id
                      AND assignment.deleted_at IS NULL
                ),
                roles_aggregated AS (
                    SELECT
                        member_roles.member_id AS member_id,
                        array_agg(member_roles.granted_role_id ORDER BY member_roles.granted_role_name)
                            AS aggregated_role_ids,
                        array_agg(member_roles.granted_role_name::text ORDER BY member_roles.granted_role_name)
                            AS aggregated_role_names
                    FROM member_roles
                    GROUP BY member_roles.member_id
                ),
                member_capabilities AS (
                    SELECT DISTINCT
                        member_roles.member_id AS member_id,
                        capability.name AS granted_capability_name
                    FROM member_roles
                    INNER JOIN foundation.role_capabilities AS grant_record
                        ON grant_record.company_id = p_company_id
                       AND grant_record.role_id = member_roles.granted_role_id
                       AND grant_record.scope = 'company'
                       AND grant_record.deleted_at IS NULL
                    INNER JOIN foundation.capabilities AS capability
                        ON capability.id = grant_record.capability_id
                ),
                capabilities_aggregated AS (
                    SELECT
                        member_capabilities.member_id AS member_id,
                        array_agg(
                            member_capabilities.granted_capability_name::text
                            ORDER BY member_capabilities.granted_capability_name)
                            AS aggregated_capability_names
                    FROM member_capabilities
                    GROUP BY member_capabilities.member_id
                )
                SELECT
                    member_row.membership_id,
                    member_row.display_name,
                    COALESCE(roles_aggregated.aggregated_role_ids, '{}'::uuid[]),
                    COALESCE(roles_aggregated.aggregated_role_names, '{}'::text[]),
                    COALESCE(capabilities_aggregated.aggregated_capability_names, '{}'::text[]),
                    -- varchar(30) precisa do cast: RETURN QUERY exige o tipo exato
                    -- da coluna declarada (text) do resultado da função.
                    invitation.status::text
                FROM (
                    SELECT
                        membership.id AS membership_id,
                        min(app_user.display_name)::text AS display_name
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.users AS app_user
                        ON app_user.id = membership.user_id
                    WHERE membership.company_id = p_company_id
                      AND membership.deleted_at IS NULL
                    GROUP BY membership.id
                ) AS member_row
                LEFT JOIN roles_aggregated
                    ON roles_aggregated.member_id = member_row.membership_id
                LEFT JOIN capabilities_aggregated
                    ON capabilities_aggregated.member_id = member_row.membership_id
                LEFT JOIN foundation.pending_member_invitations AS invitation
                    ON invitation.membership_id = member_row.membership_id
                ORDER BY member_row.display_name, member_row.membership_id;
            END;
            $function$;

            REVOKE ALL ON FUNCTION foundation.create_pending_member_invitation(
                uuid, uuid, text, text, text) FROM PUBLIC, vinx_migrator;
            REVOKE ALL ON FUNCTION foundation.list_company_members_with_roles(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.create_pending_member_invitation(
                uuid, uuid, text, text, text) TO vinx_runtime;
            GRANT EXECUTE ON FUNCTION foundation.list_company_members_with_roles(uuid)
                TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
            """
        );

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner;

            SET ROLE vinx_permissions_owner;
            CREATE POLICY pending_member_invitations_permissions_owner_maintenance_select
                ON foundation.pending_member_invitations FOR SELECT TO vinx_permissions_owner
                USING (true);
            RESET ROLE;
            CREATE POLICY idempotency_permissions_owner_maintenance_select
                ON foundation.idempotency_records FOR SELECT TO vinx_permissions_owner
                USING (true);

            SET ROLE vinx_permissions_owner;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM foundation.pending_member_invitations)
                    OR EXISTS (
                        SELECT 1
                        FROM foundation.idempotency_records
                        WHERE command_name = 'foundation.member.invite') THEN
                    RAISE EXCEPTION 'cannot downgrade member invitations while state exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            CREATE OR REPLACE FUNCTION foundation.list_company_members_with_roles(p_company_id uuid)
            RETURNS TABLE (
                membership_id uuid,
                display_name text,
                role_ids uuid[],
                role_names text[],
                capability_names text[],
                invitation_status text)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
            BEGIN
                IF p_company_id IS NULL THEN
                    RAISE EXCEPTION 'member permissions parameters are invalid'
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
                           AND capability.name = 'foundation.roles.assign'
                        WHERE membership.company_id = p_company_id
                          AND membership.user_id = v_actor_user_id
                          AND membership.deleted_at IS NULL) THEN
                    RAISE EXCEPTION 'member permissions capability is required'
                        USING ERRCODE = 'PX425';
                END IF;

                RETURN QUERY
                WITH member_roles AS (
                    SELECT
                        assignment.membership_id AS member_id,
                        role_record.id AS granted_role_id,
                        role_record.name AS granted_role_name
                    FROM foundation.role_assignments AS assignment
                    INNER JOIN foundation.roles AS role_record
                        ON role_record.company_id = assignment.company_id
                       AND role_record.id = assignment.role_id
                       AND role_record.deleted_at IS NULL
                    WHERE assignment.company_id = p_company_id
                      AND assignment.deleted_at IS NULL
                ),
                roles_aggregated AS (
                    SELECT
                        member_roles.member_id AS member_id,
                        array_agg(member_roles.granted_role_id ORDER BY member_roles.granted_role_name)
                            AS aggregated_role_ids,
                        array_agg(member_roles.granted_role_name::text ORDER BY member_roles.granted_role_name)
                            AS aggregated_role_names
                    FROM member_roles
                    GROUP BY member_roles.member_id
                ),
                member_capabilities AS (
                    SELECT DISTINCT
                        member_roles.member_id AS member_id,
                        capability.name AS granted_capability_name
                    FROM member_roles
                    INNER JOIN foundation.role_capabilities AS grant_record
                        ON grant_record.company_id = p_company_id
                       AND grant_record.role_id = member_roles.granted_role_id
                       AND grant_record.scope = 'company'
                       AND grant_record.deleted_at IS NULL
                    INNER JOIN foundation.capabilities AS capability
                        ON capability.id = grant_record.capability_id
                ),
                capabilities_aggregated AS (
                    SELECT
                        member_capabilities.member_id AS member_id,
                        array_agg(
                            member_capabilities.granted_capability_name::text
                            ORDER BY member_capabilities.granted_capability_name)
                            AS aggregated_capability_names
                    FROM member_capabilities
                    GROUP BY member_capabilities.member_id
                )
                SELECT
                    member_row.membership_id,
                    member_row.display_name,
                    COALESCE(roles_aggregated.aggregated_role_ids, '{}'::uuid[]),
                    COALESCE(roles_aggregated.aggregated_role_names, '{}'::text[]),
                    COALESCE(capabilities_aggregated.aggregated_capability_names, '{}'::text[]),
                    NULL::text
                FROM (
                    SELECT
                        membership.id AS membership_id,
                        min(app_user.display_name)::text AS display_name
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.users AS app_user
                        ON app_user.id = membership.user_id
                    WHERE membership.company_id = p_company_id
                      AND membership.deleted_at IS NULL
                    GROUP BY membership.id
                ) AS member_row
                LEFT JOIN roles_aggregated
                    ON roles_aggregated.member_id = member_row.membership_id
                LEFT JOIN capabilities_aggregated
                    ON capabilities_aggregated.member_id = member_row.membership_id
                ORDER BY member_row.display_name, member_row.membership_id;
            END;
            $function$;

            REVOKE ALL ON FUNCTION foundation.list_company_members_with_roles(uuid)
                FROM PUBLIC, vinx_migrator;
            REVOKE ALL ON FUNCTION foundation.create_pending_member_invitation(
                uuid, uuid, text, text, text) FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION foundation.create_pending_member_invitation(uuid, uuid, text, text, text);
            RESET ROLE;

            DROP POLICY idempotency_permissions_owner_maintenance_select
                ON foundation.idempotency_records;
            SET ROLE vinx_permissions_owner;
            DROP POLICY pending_member_invitations_permissions_owner_maintenance_select
                ON foundation.pending_member_invitations;
            DROP POLICY pending_member_invitations_permissions_owner_select
                ON foundation.pending_member_invitations;
            DROP POLICY pending_member_invitations_permissions_owner_insert
                ON foundation.pending_member_invitations;
            DROP TABLE foundation.pending_member_invitations;
            RESET ROLE;

            DROP POLICY idempotency_permissions_owner_insert ON foundation.idempotency_records;
            DROP POLICY idempotency_permissions_owner_select ON foundation.idempotency_records;
            DROP POLICY employees_permissions_owner_insert ON foundation.employees;
            DROP POLICY memberships_permissions_owner_insert ON foundation.memberships;

            SET ROLE vinx_audit_owner;
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_permissions_owner;
            RESET ROLE;
            REVOKE USAGE ON SCHEMA audit FROM vinx_permissions_owner;
            REVOKE REFERENCES (id) ON foundation.users, foundation.memberships
                FROM vinx_permissions_owner;
            REVOKE SELECT ON foundation.idempotency_records FROM vinx_permissions_owner;
            REVOKE INSERT ON foundation.users, foundation.memberships, foundation.employees,
                foundation.idempotency_records FROM vinx_permissions_owner;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
            """
        );
}
