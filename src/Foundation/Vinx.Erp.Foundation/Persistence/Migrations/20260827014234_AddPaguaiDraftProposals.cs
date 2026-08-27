using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaguaiDraftProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_proposal_id",
                schema: "paguai",
                table: "requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_requests_company_id_id",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "id" });

            migrationBuilder.CreateTable(
                name: "draft_proposals",
                schema: "paguai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    beneficiary_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    proof_requirement = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    source_references = table.Column<string>(type: "jsonb", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    result_draft_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_draft_proposals", x => x.id);
                    table.UniqueConstraint("ak_draft_proposals_company_id_id", x => new { x.company_id, x.id });
                    table.CheckConstraint("ck_paguai_draft_proposals_confidence", "confidence >= 0 AND confidence <= 1");
                    table.CheckConstraint("ck_paguai_draft_proposals_currency_brl", "currency = 'BRL'");
                    table.CheckConstraint("ck_paguai_draft_proposals_lifetime", "expires_at = created_at + interval '15 minutes'");
                    table.CheckConstraint("ck_paguai_draft_proposals_resolution", "(state = 'pending' AND resolved_at IS NULL AND resolution_request_id IS NULL AND result_draft_id IS NULL) OR (state = 'consumed' AND resolved_at IS NOT NULL AND resolution_request_id IS NOT NULL AND result_draft_id IS NOT NULL) OR (state IN ('rejected', 'invalidated') AND resolved_at IS NOT NULL AND resolution_request_id IS NOT NULL AND result_draft_id IS NULL)");
                    table.CheckConstraint("ck_paguai_draft_proposals_state", "state IN ('pending', 'consumed', 'rejected', 'invalidated')");
                    table.ForeignKey(
                        name: "fk_draft_proposals_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_draft_proposals_files_company_id_file_record_id",
                        columns: x => new { x.company_id, x.file_record_id },
                        principalSchema: "foundation",
                        principalTable: "files",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_draft_proposals_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalSchema: "foundation",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_requests_company_id_source_proposal_id",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "source_proposal_id" });

            migrationBuilder.CreateIndex(
                name: "uq_paguai_requests_source_proposal_id",
                schema: "paguai",
                table: "requests",
                column: "source_proposal_id",
                unique: true,
                filter: "source_proposal_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_draft_proposals_actor_user_id",
                schema: "paguai",
                table: "draft_proposals",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_draft_proposals_company_id_file_record_id",
                schema: "paguai",
                table: "draft_proposals",
                columns: new[] { "company_id", "file_record_id" });

            migrationBuilder.CreateIndex(
                name: "uq_paguai_draft_proposals_live_request",
                schema: "paguai",
                table: "draft_proposals",
                columns: new[] { "company_id", "actor_user_id", "request_id" },
                unique: true,
                filter: "state = 'pending'");

            migrationBuilder.AddForeignKey(
                name: "fk_requests_draft_proposals_company_id_source_proposal_id",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "source_proposal_id" },
                principalSchema: "paguai",
                principalTable: "draft_proposals",
                principalColumns: new[] { "company_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                ALTER TABLE paguai.draft_proposals ENABLE ROW LEVEL SECURITY;
                ALTER TABLE paguai.draft_proposals FORCE ROW LEVEL SECURITY;

                CREATE POLICY paguai_draft_proposals_actor_company_isolation
                ON paguai.draft_proposals
                USING (
                    company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                )
                WITH CHECK (
                    company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                );

                REVOKE ALL ON TABLE paguai.draft_proposals FROM PUBLIC;
                GRANT USAGE ON SCHEMA paguai TO vinx_runtime;
                GRANT SELECT, INSERT, UPDATE ON TABLE paguai.draft_proposals TO vinx_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_requests_draft_proposals_company_id_source_proposal_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropTable(
                name: "draft_proposals",
                schema: "paguai");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_requests_company_id_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "ix_requests_company_id_source_proposal_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "uq_paguai_requests_source_proposal_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "source_proposal_id",
                schema: "paguai",
                table: "requests");
        }
    }
}
