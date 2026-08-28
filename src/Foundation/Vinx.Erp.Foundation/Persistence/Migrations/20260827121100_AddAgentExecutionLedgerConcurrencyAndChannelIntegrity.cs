using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentExecutionLedgerConcurrencyAndChannelIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "row_version",
                schema: "foundation",
                table: "agent_execution_ledger",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_execution_ledger_channel_inbound_message",
                schema: "foundation",
                table: "agent_execution_ledger",
                sql: "(channel = 'web' AND inbound_message_id IS NULL) OR (channel IN ('whatsapp', 'telegram') AND inbound_message_id IS NOT NULL)");

            migrationBuilder.Sql(
                "GRANT UPDATE (row_version) ON TABLE foundation.agent_execution_ledger TO vinx_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE (row_version) ON TABLE foundation.agent_execution_ledger FROM vinx_runtime;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_execution_ledger_channel_inbound_message",
                schema: "foundation",
                table: "agent_execution_ledger");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "foundation",
                table: "agent_execution_ledger");
        }
    }
}
