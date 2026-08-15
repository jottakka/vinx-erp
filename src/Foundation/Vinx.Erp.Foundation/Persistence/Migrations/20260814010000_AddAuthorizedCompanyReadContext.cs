using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorizedCompanyReadContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION foundation.current_authorized_company_ids()
                RETURNS SETOF uuid
                LANGUAGE sql
                STABLE
                SET search_path TO foundation, pg_catalog
                AS $function$
                    SELECT value::uuid
                    FROM regexp_split_to_table(
                        NULLIF(current_setting('app.authorized_company_ids', true), ''), ',') AS value
                    WHERE value <> '';
                $function$;

                REVOKE ALL ON FUNCTION foundation.current_authorized_company_ids() FROM PUBLIC, vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.current_authorized_company_ids() TO vinx_runtime;

                DROP POLICY companies_company_isolation ON foundation.companies;
                CREATE POLICY companies_authorized_company_read ON foundation.companies
                    FOR SELECT USING (
                        id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY companies_company_isolation ON foundation.companies
                    FOR ALL USING (id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY employees_company_isolation ON foundation.employees;
                CREATE POLICY employees_authorized_company_read ON foundation.employees
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY employees_company_isolation ON foundation.employees
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY files_company_isolation ON foundation.files;
                CREATE POLICY files_authorized_company_read ON foundation.files
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY files_company_isolation ON foundation.files
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY groups_company_isolation ON foundation.groups;
                CREATE POLICY groups_authorized_company_read ON foundation.groups
                    FOR SELECT USING (
                        id IN (
                            SELECT company.group_id
                            FROM foundation.companies AS company
                            WHERE company.id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                               OR company.id IN (SELECT foundation.current_authorized_company_ids())));
                CREATE POLICY groups_company_isolation ON foundation.groups
                    FOR ALL USING (id = (
                        SELECT company.group_id
                        FROM foundation.companies AS company
                        WHERE company.id = (NULLIF(current_setting('app.company_id', true), '')::uuid)))
                    WITH CHECK (id = (
                        SELECT company.group_id
                        FROM foundation.companies AS company
                        WHERE company.id = (NULLIF(current_setting('app.company_id', true), '')::uuid)));

                DROP POLICY idempotency_records_company_isolation ON foundation.idempotency_records;
                CREATE POLICY idempotency_records_authorized_company_read ON foundation.idempotency_records
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY idempotency_records_company_isolation ON foundation.idempotency_records
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY memberships_company_isolation ON foundation.memberships;
                CREATE POLICY memberships_authorized_company_read ON foundation.memberships
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY memberships_company_isolation ON foundation.memberships
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY role_assignments_company_isolation ON foundation.role_assignments;
                CREATE POLICY role_assignments_authorized_company_read ON foundation.role_assignments
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY role_assignments_company_isolation ON foundation.role_assignments
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY role_capabilities_company_isolation ON foundation.role_capabilities;
                CREATE POLICY role_capabilities_authorized_company_read ON foundation.role_capabilities
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY role_capabilities_company_isolation ON foundation.role_capabilities
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY roles_company_isolation ON foundation.roles;
                CREATE POLICY roles_authorized_company_read ON foundation.roles
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY roles_company_isolation ON foundation.roles
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY teams_company_isolation ON foundation.teams;
                CREATE POLICY teams_authorized_company_read ON foundation.teams
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY teams_company_isolation ON foundation.teams
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY units_company_isolation ON foundation.units;
                CREATE POLICY units_authorized_company_read ON foundation.units
                    FOR SELECT USING (
                        company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid)
                        OR company_id IN (SELECT foundation.current_authorized_company_ids()));
                CREATE POLICY units_company_isolation ON foundation.units
                    FOR ALL USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY companies_company_isolation ON foundation.companies;
                DROP POLICY companies_authorized_company_read ON foundation.companies;
                CREATE POLICY companies_company_isolation ON foundation.companies
                    USING (id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY employees_company_isolation ON foundation.employees;
                DROP POLICY employees_authorized_company_read ON foundation.employees;
                CREATE POLICY employees_company_isolation ON foundation.employees
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY files_company_isolation ON foundation.files;
                DROP POLICY files_authorized_company_read ON foundation.files;
                CREATE POLICY files_company_isolation ON foundation.files
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY groups_company_isolation ON foundation.groups;
                DROP POLICY groups_authorized_company_read ON foundation.groups;
                CREATE POLICY groups_company_isolation ON foundation.groups
                    USING (id = (
                        SELECT company.group_id
                        FROM foundation.companies AS company
                        WHERE company.id = (NULLIF(current_setting('app.company_id', true), '')::uuid)))
                    WITH CHECK (id = (
                        SELECT company.group_id
                        FROM foundation.companies AS company
                        WHERE company.id = (NULLIF(current_setting('app.company_id', true), '')::uuid)));

                DROP POLICY idempotency_records_company_isolation ON foundation.idempotency_records;
                DROP POLICY idempotency_records_authorized_company_read ON foundation.idempotency_records;
                CREATE POLICY idempotency_records_company_isolation ON foundation.idempotency_records
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY memberships_company_isolation ON foundation.memberships;
                DROP POLICY memberships_authorized_company_read ON foundation.memberships;
                CREATE POLICY memberships_company_isolation ON foundation.memberships
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY role_assignments_company_isolation ON foundation.role_assignments;
                DROP POLICY role_assignments_authorized_company_read ON foundation.role_assignments;
                CREATE POLICY role_assignments_company_isolation ON foundation.role_assignments
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY role_capabilities_company_isolation ON foundation.role_capabilities;
                DROP POLICY role_capabilities_authorized_company_read ON foundation.role_capabilities;
                CREATE POLICY role_capabilities_company_isolation ON foundation.role_capabilities
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY roles_company_isolation ON foundation.roles;
                DROP POLICY roles_authorized_company_read ON foundation.roles;
                CREATE POLICY roles_company_isolation ON foundation.roles
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY teams_company_isolation ON foundation.teams;
                DROP POLICY teams_authorized_company_read ON foundation.teams;
                CREATE POLICY teams_company_isolation ON foundation.teams
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                DROP POLICY units_company_isolation ON foundation.units;
                DROP POLICY units_authorized_company_read ON foundation.units;
                CREATE POLICY units_company_isolation ON foundation.units
                    USING (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (company_id = (NULLIF(current_setting('app.company_id', true), '')::uuid));

                REVOKE ALL ON FUNCTION foundation.current_authorized_company_ids() FROM PUBLIC, vinx_runtime;
                DROP FUNCTION foundation.current_authorized_company_ids();
                """);
        }
    }
}
