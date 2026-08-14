using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantRuntimeMembershipRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT UPDATE (
                    deleted_at,
                    deleted_by_user_id,
                    deleted_reason,
                    updated_by_user_id,
                    updated_at,
                    request_id,
                    row_version)
                ON TABLE foundation.memberships TO vinx_runtime;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE UPDATE (
                    deleted_at,
                    deleted_by_user_id,
                    deleted_reason,
                    updated_by_user_id,
                    updated_at,
                    request_id,
                    row_version)
                ON TABLE foundation.memberships FROM vinx_runtime;
                """);

        }
    }
}
