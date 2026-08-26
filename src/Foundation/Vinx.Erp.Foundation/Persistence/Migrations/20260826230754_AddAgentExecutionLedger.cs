using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentExecutionLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_execution_ledger",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    safe_result_json = table.Column<string>(type: "jsonb", nullable: true),
                    reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_execution_ledger", x => x.id);
                    table.UniqueConstraint("ak_agent_execution_ledger_company_id_id", x => new { x.company_id, x.id });
                    table.CheckConstraint("ck_agent_execution_ledger_payload_hash_format", "payload_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_agent_execution_ledger_state", "state IN ('claimed', 'vision_in_flight', 'model_in_flight', 'completed', 'failed_closed', 'ambiguous')");
                    table.CheckConstraint("ck_agent_execution_ledger_terminal_data", "(state = 'completed' AND safe_result_json IS NOT NULL AND reason_code IS NULL) OR (state IN ('failed_closed', 'ambiguous') AND safe_result_json IS NULL AND reason_code IS NOT NULL) OR (state IN ('claimed', 'vision_in_flight', 'model_in_flight') AND safe_result_json IS NULL AND reason_code IS NULL)");
                    table.ForeignKey(
                        name: "fk_agent_execution_ledger_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_execution_ledger_company_id_actor_user_id_channel_request_id",
                schema: "foundation",
                table: "agent_execution_ledger",
                columns: new[] { "company_id", "actor_user_id", "channel", "request_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE foundation.agent_execution_ledger ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.agent_execution_ledger FORCE ROW LEVEL SECURITY;
                CREATE POLICY agent_execution_ledger_company_isolation
                    ON foundation.agent_execution_ledger
                    USING (
                        company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                        AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid)
                    WITH CHECK (
                        company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                        AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid);

                REVOKE ALL ON TABLE foundation.agent_execution_ledger FROM PUBLIC, vinx_runtime;
                GRANT SELECT, INSERT ON TABLE foundation.agent_execution_ledger TO vinx_runtime;
                GRANT UPDATE (state, safe_result_json, reason_code, updated_at)
                    ON TABLE foundation.agent_execution_ledger TO vinx_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_execution_ledger",
                schema: "foundation");
        }
    }
}
