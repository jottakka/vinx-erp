using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyDefaultRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_permissions_owner;
                GRANT INSERT ON foundation.roles, foundation.role_capabilities
                    TO vinx_permissions_owner;
                GRANT SELECT ON foundation.companies, foundation.roles, foundation.role_capabilities,
                    foundation.capabilities, foundation.memberships TO vinx_permissions_owner;
                GRANT REFERENCES (id) ON foundation.companies TO vinx_permissions_owner;
                GRANT EXECUTE ON FUNCTION foundation.current_authorized_company_ids()
                    TO vinx_permissions_owner;

                CREATE POLICY roles_permissions_owner_insert
                    ON foundation.roles FOR INSERT TO vinx_permissions_owner
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
                CREATE POLICY roles_permissions_owner_select
                    ON foundation.roles FOR SELECT TO vinx_permissions_owner
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
                CREATE POLICY role_capabilities_permissions_owner_insert
                    ON foundation.role_capabilities
                    FOR INSERT TO vinx_permissions_owner
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
                CREATE POLICY role_capabilities_permissions_owner_select
                    ON foundation.role_capabilities FOR SELECT TO vinx_permissions_owner
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
                CREATE POLICY capabilities_permissions_owner_select
                    ON foundation.capabilities FOR SELECT TO vinx_permissions_owner
                    USING (name IN ('foundation.roles.assign', 'foundation.employees.create',
                        'foundation.files.upload', 'foundation.files.confirm'));
                CREATE POLICY memberships_permissions_owner_select
                    ON foundation.memberships FOR SELECT TO vinx_permissions_owner
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                CREATE POLICY companies_permissions_owner_backfill_select
                    ON foundation.companies FOR SELECT TO vinx_permissions_owner
                    USING (deleted_at IS NULL);
                SET ROLE vinx_permissions_owner;
                CREATE FUNCTION foundation.ensure_company_default_roles(p_company_id uuid)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO pg_catalog, foundation, pg_temp
                AS $function$
                DECLARE
                    v_seed_actor uuid;
                    v_role_id uuid;
                    v_grant_count integer;
                BEGIN
                    IF p_company_id IS NULL THEN
                        RAISE EXCEPTION 'company default roles parameters are invalid'
                            USING ERRCODE = '22023';
                    END IF;

                    PERFORM set_config('app.company_id', p_company_id::text, true);

                    SELECT membership.user_id
                    INTO v_seed_actor
                    FROM foundation.memberships AS membership
                    WHERE membership.company_id = p_company_id
                      AND membership.deleted_at IS NULL
                    ORDER BY membership.created_at, membership.id
                    LIMIT 1;
                    IF v_seed_actor IS NULL THEN
                        RAISE EXCEPTION 'company has no active membership'
                            USING ERRCODE = '23514';
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM foundation.roles AS role_record
                        WHERE role_record.company_id = p_company_id
                          AND role_record.name = 'admin'
                          AND role_record.deleted_at IS NULL) THEN
                        v_role_id := gen_random_uuid();
                        INSERT INTO foundation.roles (
                            id, company_id, name, created_by_user_id, updated_by_user_id,
                            request_id, created_at, updated_at)
                        VALUES (
                            v_role_id, p_company_id, 'admin', v_seed_actor, v_seed_actor,
                            gen_random_uuid(), clock_timestamp(), clock_timestamp());
                        INSERT INTO foundation.role_capabilities (
                            id, company_id, role_id, capability_id, scope,
                            created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                        SELECT
                            gen_random_uuid(),
                            p_company_id,
                            v_role_id,
                            capability.id,
                            'company',
                            v_seed_actor,
                            v_seed_actor,
                            gen_random_uuid(),
                            clock_timestamp(),
                            clock_timestamp()
                        FROM foundation.capabilities AS capability
                        WHERE capability.name IN (
                            'foundation.roles.assign',
                            'foundation.employees.create',
                            'foundation.files.upload',
                            'foundation.files.confirm')
                        ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                            DO NOTHING;
                        GET DIAGNOSTICS v_grant_count = ROW_COUNT;
                        IF v_grant_count <> 4 THEN
                            RAISE EXCEPTION 'admin default role capability catalog is incomplete'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM foundation.roles AS role_record
                        WHERE role_record.company_id = p_company_id
                          AND role_record.name = 'member'
                          AND role_record.deleted_at IS NULL) THEN
                        v_role_id := gen_random_uuid();
                        INSERT INTO foundation.roles (
                            id, company_id, name, created_by_user_id, updated_by_user_id,
                            request_id, created_at, updated_at)
                        VALUES (
                            v_role_id, p_company_id, 'member', v_seed_actor, v_seed_actor,
                            gen_random_uuid(), clock_timestamp(), clock_timestamp());
                        INSERT INTO foundation.role_capabilities (
                            id, company_id, role_id, capability_id, scope,
                            created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                        SELECT
                            gen_random_uuid(),
                            p_company_id,
                            v_role_id,
                            capability.id,
                            'company',
                            v_seed_actor,
                            v_seed_actor,
                            gen_random_uuid(),
                            clock_timestamp(),
                            clock_timestamp()
                        FROM foundation.capabilities AS capability
                        WHERE capability.name IN ('foundation.files.upload')
                        ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                            DO NOTHING;
                        GET DIAGNOSTICS v_grant_count = ROW_COUNT;
                        IF v_grant_count <> 1 THEN
                            RAISE EXCEPTION 'member default role capability catalog is incomplete'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                END;
                $function$;
                REVOKE ALL ON FUNCTION foundation.ensure_company_default_roles(uuid)
                    FROM PUBLIC, vinx_migrator;
                GRANT EXECUTE ON FUNCTION foundation.ensure_company_default_roles(uuid)
                    TO vinx_runtime;

                -- Empresas sem membership ativo (ex.: dados legados) não têm ator
                -- para semear papéis e são ignoradas; o guard interno da função
                -- permanece para os chamadores de provisionamento, que sempre têm
                -- o owner. O RLS de memberships exige app.company_id, por isso o
                -- bloco define o contexto por empresa antes da checagem.
                DO $block$
                DECLARE
                    company_record RECORD;
                    has_active_membership boolean;
                BEGIN
                    FOR company_record IN
                        SELECT id
                        FROM foundation.companies
                        WHERE deleted_at IS NULL
                    LOOP
                        PERFORM set_config('app.company_id', company_record.id::text, true);
                        SELECT EXISTS (
                            SELECT 1
                            FROM foundation.memberships
                            WHERE company_id = company_record.id
                              AND deleted_at IS NULL)
                        INTO has_active_membership;
                        IF has_active_membership THEN
                            PERFORM foundation.ensure_company_default_roles(company_record.id);
                        END IF;
                    END LOOP;
                END;
                $block$;
                RESET ROLE;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
                DROP POLICY companies_permissions_owner_backfill_select ON foundation.companies;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE POLICY companies_permissions_owner_maintenance_select
                    ON foundation.companies FOR SELECT TO vinx_permissions_owner
                    USING (true);
                CREATE POLICY roles_permissions_owner_maintenance_select
                    ON foundation.roles FOR SELECT TO vinx_permissions_owner
                    USING (name IN ('admin', 'member'));
                CREATE POLICY role_capabilities_permissions_owner_maintenance_select
                    ON foundation.role_capabilities FOR SELECT TO vinx_permissions_owner
                    USING (EXISTS (
                        SELECT 1
                        FROM foundation.capabilities AS capability
                        WHERE capability.id = capability_id
                          AND capability.name IN (
                              'foundation.roles.assign', 'foundation.employees.create',
                              'foundation.files.upload', 'foundation.files.confirm')));
                SET ROLE vinx_permissions_owner;
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM foundation.role_capabilities AS grant_record
                        INNER JOIN foundation.roles AS role_record
                            ON role_record.id = grant_record.role_id
                        INNER JOIN foundation.capabilities AS capability
                            ON capability.id = grant_record.capability_id
                        WHERE grant_record.company_id IN (SELECT id FROM foundation.companies)
                          AND role_record.name IN ('admin', 'member')
                          AND role_record.deleted_at IS NULL
                          AND grant_record.deleted_at IS NULL
                          AND capability.name IN (
                              'foundation.roles.assign', 'foundation.employees.create',
                              'foundation.files.upload', 'foundation.files.confirm')) THEN
                        RAISE EXCEPTION 'cannot downgrade company default roles while state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;
                RESET ROLE;
                DROP POLICY role_capabilities_permissions_owner_maintenance_select
                    ON foundation.role_capabilities;
                DROP POLICY roles_permissions_owner_maintenance_select
                    ON foundation.roles;
                DROP POLICY companies_permissions_owner_maintenance_select
                    ON foundation.companies;

                SET ROLE vinx_permissions_owner;
                REVOKE ALL ON FUNCTION foundation.ensure_company_default_roles(uuid)
                    FROM PUBLIC, vinx_runtime, vinx_migrator;
                DROP FUNCTION foundation.ensure_company_default_roles(uuid);
                RESET ROLE;

                DROP POLICY memberships_permissions_owner_select ON foundation.memberships;
                DROP POLICY capabilities_permissions_owner_select ON foundation.capabilities;
                DROP POLICY role_capabilities_permissions_owner_select ON foundation.role_capabilities;
                DROP POLICY role_capabilities_permissions_owner_insert ON foundation.role_capabilities;
                DROP POLICY roles_permissions_owner_select ON foundation.roles;
                DROP POLICY roles_permissions_owner_insert ON foundation.roles;

                REVOKE REFERENCES (id) ON foundation.companies FROM vinx_permissions_owner;
                REVOKE EXECUTE ON FUNCTION foundation.current_authorized_company_ids()
                    FROM vinx_permissions_owner;
                REVOKE SELECT ON foundation.companies, foundation.roles, foundation.role_capabilities,
                    foundation.capabilities, foundation.memberships FROM vinx_permissions_owner;
                REVOKE INSERT ON foundation.roles, foundation.role_capabilities
                    FROM vinx_permissions_owner;
                REVOKE USAGE ON SCHEMA foundation FROM vinx_permissions_owner;
                """);
        }
    }
}
