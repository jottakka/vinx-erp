using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoundationApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmation_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                    table.UniqueConstraint("ak_approvals_company_id_id", x => new { x.company_id, x.id });
                    table.CheckConstraint("ck_approvals_expires_after_creation", "expires_at > created_at");
                    table.CheckConstraint("ck_approvals_payload_hash_format", "payload_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_approvals_veto_complete", "(deleted_at IS NULL AND deleted_by_user_id IS NULL AND deleted_reason IS NULL) OR (deleted_at IS NOT NULL AND deleted_by_user_id IS NOT NULL AND deleted_reason = 'approval_vetoed')");
                    table.ForeignKey(
                        name: "fk_approvals_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalSchema: "foundation",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_actor_user_id",
                schema: "foundation",
                table: "approvals",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_company_id_actor_user_id",
                schema: "foundation",
                table: "approvals",
                columns: new[] { "company_id", "actor_user_id" },
                filter: "consumed_at IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_confirmation_request_id",
                schema: "foundation",
                table: "approvals",
                column: "confirmation_request_id",
                unique: true,
                filter: "confirmation_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_proposal_id",
                schema: "foundation",
                table: "approvals",
                column: "proposal_id",
                unique: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE foundation.approvals ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.approvals FORCE ROW LEVEL SECURITY;
                CREATE POLICY approvals_company_isolation ON foundation.approvals
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                        AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                        AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid);

                REVOKE ALL ON TABLE foundation.approvals FROM vinx_runtime;
                GRANT SELECT, INSERT ON TABLE foundation.approvals TO vinx_runtime;
                GRANT UPDATE (
                    confirmation_request_id,
                    consumed_at,
                    result_json,
                    updated_by_user_id,
                    updated_at,
                    request_id,
                    row_version,
                    deleted_at,
                    deleted_by_user_id,
                    deleted_reason)
                ON TABLE foundation.approvals TO vinx_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE foundation.approvals NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE foundation.approvals DISABLE ROW LEVEL SECURITY;

                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM foundation.approvals) THEN
                        RAISE EXCEPTION 'cannot downgrade Foundation approvals while approval state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                DROP POLICY approvals_company_isolation ON foundation.approvals;
                REVOKE ALL ON TABLE foundation.approvals FROM vinx_runtime;
                """);

            migrationBuilder.DropTable(
                name: "approvals",
                schema: "foundation");
        }
    }
}
