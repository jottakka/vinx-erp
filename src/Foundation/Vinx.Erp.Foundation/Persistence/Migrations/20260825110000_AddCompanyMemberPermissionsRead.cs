using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyMemberPermissionsRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT SELECT ON foundation.role_assignments TO vinx_permissions_owner;
                -- Somente as colunas não sensíveis de users: email nunca é concedido ao papel.
                GRANT SELECT (id, display_name) ON foundation.users TO vinx_permissions_owner;

                CREATE POLICY role_assignments_permissions_owner_select
                    ON foundation.role_assignments FOR SELECT TO vinx_permissions_owner
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
                -- Catálogo de capabilities legível além das sementes: toda capability concedida
                -- por grant vivo precisa ser visível para a listagem, sem expor o resto do catálogo.
                CREATE POLICY capabilities_permissions_owner_granted_select
                    ON foundation.capabilities FOR SELECT TO vinx_permissions_owner
                    USING (EXISTS (
                        SELECT 1
                        FROM foundation.role_capabilities AS grant_record
                        WHERE grant_record.capability_id = capabilities.id
                          AND grant_record.deleted_at IS NULL));

                -- Funções de leitura pertencem a vinx_permissions_owner: CREATE é
                -- concedido apenas na janela de criação e revogado em seguida.
                GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner;
                SET ROLE vinx_permissions_owner;
                CREATE FUNCTION foundation.list_company_members_with_roles(p_company_id uuid)
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
                            USING ERRCODE = '42501';
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

                CREATE FUNCTION foundation.list_company_roles_with_grants(p_company_id uuid)
                RETURNS TABLE (role_id uuid, name text, capability_names text[])
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
                            USING ERRCODE = '42501';
                    END IF;

                    RETURN QUERY
                    WITH grants_aggregated AS (
                        SELECT
                            grant_record.role_id AS granted_role_id,
                            array_agg(capability.name::text ORDER BY capability.name)
                                AS aggregated_capability_names
                        FROM foundation.role_capabilities AS grant_record
                        INNER JOIN foundation.capabilities AS capability
                            ON capability.id = grant_record.capability_id
                        WHERE grant_record.company_id = p_company_id
                          AND grant_record.scope = 'company'
                          AND grant_record.deleted_at IS NULL
                        GROUP BY grant_record.role_id
                    )
                    SELECT
                        role_record.id,
                        role_record.name::text,
                        COALESCE(grants_aggregated.aggregated_capability_names, '{}'::text[])
                    FROM foundation.roles AS role_record
                    LEFT JOIN grants_aggregated
                        ON grants_aggregated.granted_role_id = role_record.id
                    WHERE role_record.company_id = p_company_id
                      AND role_record.deleted_at IS NULL
                    ORDER BY role_record.name, role_record.id;
                END;
                $function$;

                REVOKE ALL ON FUNCTION foundation.list_company_members_with_roles(uuid)
                    FROM PUBLIC, vinx_migrator;
                REVOKE ALL ON FUNCTION foundation.list_company_roles_with_grants(uuid)
                    FROM PUBLIC, vinx_migrator;
                GRANT EXECUTE ON FUNCTION foundation.list_company_members_with_roles(uuid)
                    TO vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.list_company_roles_with_grants(uuid)
                    TO vinx_runtime;
                RESET ROLE;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ROLE vinx_permissions_owner;
                REVOKE ALL ON FUNCTION foundation.list_company_members_with_roles(uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                REVOKE ALL ON FUNCTION foundation.list_company_roles_with_grants(uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                DROP FUNCTION foundation.list_company_members_with_roles(uuid);
                DROP FUNCTION foundation.list_company_roles_with_grants(uuid);
                RESET ROLE;

                DROP POLICY capabilities_permissions_owner_granted_select ON foundation.capabilities;
                DROP POLICY role_assignments_permissions_owner_select ON foundation.role_assignments;
                REVOKE SELECT (id, display_name) ON foundation.users FROM vinx_permissions_owner;
                REVOKE SELECT ON foundation.role_assignments FROM vinx_permissions_owner;
                """
            );
        }
    }
}
