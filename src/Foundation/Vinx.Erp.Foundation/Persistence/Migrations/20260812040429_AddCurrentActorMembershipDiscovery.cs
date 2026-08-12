using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrentActorMembershipDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_authorization_owner;
                GRANT SELECT ON TABLE foundation.memberships, foundation.companies
                    TO vinx_authorization_owner;

                CREATE POLICY memberships_authorization_owner_read
                    ON foundation.memberships
                    FOR SELECT
                    TO vinx_authorization_owner
                    USING (true);

                CREATE POLICY companies_authorization_owner_read
                    ON foundation.companies
                    FOR SELECT
                    TO vinx_authorization_owner
                    USING (true);

                CREATE FUNCTION foundation.current_actor_memberships()
                RETURNS TABLE (membership_id uuid, company_id uuid, company_name text)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path TO foundation, pg_catalog
                AS $function$
                    SELECT membership.id, company.id, company.name::text
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.companies AS company ON company.id = membership.company_id
                    WHERE membership.user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                      AND membership.deleted_at IS NULL
                      AND company.deleted_at IS NULL
                    ORDER BY company.name, company.id;
                $function$;

                REVOKE ALL ON FUNCTION foundation.current_actor_memberships()
                    FROM PUBLIC, vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.current_actor_memberships()
                    TO vinx_runtime;
                ALTER FUNCTION foundation.current_actor_memberships()
                    OWNER TO vinx_authorization_owner;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_authorization_owner;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ROLE vinx_authorization_owner;
                REVOKE ALL ON FUNCTION foundation.current_actor_memberships()
                    FROM PUBLIC, vinx_runtime;
                DROP FUNCTION foundation.current_actor_memberships();
                RESET ROLE;

                DROP POLICY companies_authorization_owner_read ON foundation.companies;
                DROP POLICY memberships_authorization_owner_read ON foundation.memberships;
                REVOKE SELECT ON TABLE foundation.memberships, foundation.companies
                    FROM vinx_authorization_owner;
                REVOKE USAGE ON SCHEMA foundation FROM vinx_authorization_owner;
                """);
        }
    }
}
