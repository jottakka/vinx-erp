using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoundationBootstrapProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_authorization_owner;
                GRANT SELECT (id, display_name) ON TABLE foundation.users
                    TO vinx_authorization_owner;

                CREATE FUNCTION foundation.current_actor_profile()
                RETURNS TABLE (user_id uuid, display_name text)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path TO foundation, pg_catalog
                AS $function$
                    SELECT user_record.id, user_record.display_name
                    FROM foundation.users AS user_record
                    WHERE user_record.id = NULLIF(current_setting('app.user_id', true), '')::uuid;
                $function$;

                REVOKE ALL ON FUNCTION foundation.current_actor_profile()
                    FROM PUBLIC, vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.current_actor_profile()
                    TO vinx_runtime;
                ALTER FUNCTION foundation.current_actor_profile()
                    OWNER TO vinx_authorization_owner;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_authorization_owner;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ROLE vinx_authorization_owner;
                REVOKE ALL ON FUNCTION foundation.current_actor_profile()
                    FROM PUBLIC, vinx_runtime;
                DROP FUNCTION foundation.current_actor_profile();
                RESET ROLE;

                REVOKE SELECT (id, display_name) ON TABLE foundation.users
                    FROM vinx_authorization_owner;
                """
            );
        }
    }
}
