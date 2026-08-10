using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInternalSessionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "row_version",
                schema: "foundation",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sessions_valid_after",
                schema: "foundation",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_sessions",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.CheckConstraint("ck_user_sessions_authenticated_before_expiration", "provider_authenticated_at < token_expires_at");
                    table.CheckConstraint("ck_user_sessions_revocation_complete", "(revoked_at IS NULL AND revoked_by_user_id IS NULL AND revocation_reason IS NULL) OR (revoked_at IS NOT NULL AND revoked_by_user_id IS NOT NULL AND revocation_reason IS NOT NULL AND btrim(revocation_reason) <> '')");
                    table.ForeignKey(
                        name: "fk_user_sessions_users_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalSchema: "foundation",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "foundation",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_issuer_provider_session_id",
                schema: "foundation",
                table: "user_sessions",
                columns: new[] { "issuer", "provider_session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_revoked_by_user_id",
                schema: "foundation",
                table: "user_sessions",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id_revoked_at",
                schema: "foundation",
                table: "user_sessions",
                columns: new[] { "user_id", "revoked_at" });

            migrationBuilder.Sql(
                """
                REVOKE ALL ON TABLE foundation.user_sessions FROM PUBLIC, vinx_runtime;
                GRANT SELECT, INSERT ON TABLE foundation.user_sessions TO vinx_runtime;
                REVOKE ALL ON TABLE foundation.users FROM vinx_runtime;
                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_identity_owner;
                GRANT SELECT ON TABLE foundation.users TO vinx_identity_owner;

                CREATE FUNCTION foundation.lock_user_for_session_admission(p_user_id uuid)
                RETURNS TABLE (sessions_valid_after timestamp with time zone)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO foundation, pg_catalog
                AS $function$
                BEGIN
                    RETURN QUERY
                    SELECT user_record.sessions_valid_after
                    FROM foundation.users AS user_record
                    WHERE user_record.id = p_user_id
                    FOR UPDATE;
                END;
                $function$;

                REVOKE ALL ON FUNCTION foundation.lock_user_for_session_admission(uuid)
                    FROM PUBLIC, vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.lock_user_for_session_admission(uuid)
                    TO vinx_runtime;
                ALTER FUNCTION foundation.lock_user_for_session_admission(uuid)
                    OWNER TO vinx_identity_owner;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_identity_owner;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE foundation.user_sessions IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE foundation.users IN ACCESS EXCLUSIVE MODE;

                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM foundation.user_sessions)
                        OR EXISTS (
                            SELECT 1
                            FROM foundation.users
                            WHERE sessions_valid_after IS NOT NULL) THEN
                        RAISE EXCEPTION 'cannot downgrade internal session state while session state exists'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                SET ROLE vinx_identity_owner;
                REVOKE ALL ON FUNCTION foundation.lock_user_for_session_admission(uuid)
                    FROM PUBLIC, vinx_runtime;
                DROP FUNCTION foundation.lock_user_for_session_admission(uuid);
                RESET ROLE;
                REVOKE SELECT ON TABLE foundation.users FROM vinx_identity_owner;
                REVOKE USAGE ON SCHEMA foundation FROM vinx_identity_owner;
                REVOKE ALL ON TABLE foundation.user_sessions FROM PUBLIC, vinx_runtime;
                """);

            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "foundation");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "foundation",
                table: "users");

            migrationBuilder.DropColumn(
                name: "sessions_valid_after",
                schema: "foundation",
                table: "users");
        }
    }
}
