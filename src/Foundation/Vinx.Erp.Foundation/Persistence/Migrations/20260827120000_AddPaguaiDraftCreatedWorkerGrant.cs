using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260827120000_AddPaguaiDraftCreatedWorkerGrant")]
public partial class AddPaguaiDraftCreatedWorkerGrant : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            GRANT EXECUTE ON FUNCTION notifications.enqueue_notification_intent(
                uuid, uuid, text, text, text, text)
                TO vinx_worker;
            RESET ROLE;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET ROLE vinx_notifications_owner;
            REVOKE EXECUTE ON FUNCTION notifications.enqueue_notification_intent(
                uuid, uuid, text, text, text, text)
                FROM vinx_worker;
            RESET ROLE;
            """
        );
}
