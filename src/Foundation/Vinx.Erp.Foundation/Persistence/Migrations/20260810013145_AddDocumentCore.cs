using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extension",
                schema: "foundation",
                table: "files",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "foundation",
                table: "files",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mime_type",
                schema: "foundation",
                table: "files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "foundation",
                table: "files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_file_name",
                schema: "foundation",
                table: "files",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_state",
                schema: "foundation",
                table: "files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retention_until",
                schema: "foundation",
                table: "files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "size_bytes",
                schema: "foundation",
                table: "files",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE foundation.files
                SET extension = '.bin',
                    mime_type = 'application/octet-stream',
                    origin = 'legacy',
                    original_file_name = 'legacy-document',
                    processing_state = CASE WHEN is_quarantined THEN 'quarantined' ELSE 'verified' END,
                    retention_until = CURRENT_TIMESTAMP + INTERVAL '30 days',
                    size_bytes = 0
                """);

            migrationBuilder.AlterColumn<string>(
                name: "extension",
                schema: "foundation",
                table: "files",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "mime_type",
                schema: "foundation",
                table: "files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "origin",
                schema: "foundation",
                table: "files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "original_file_name",
                schema: "foundation",
                table: "files",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "processing_state",
                schema: "foundation",
                table: "files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "retention_until",
                schema: "foundation",
                table: "files",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "size_bytes",
                schema: "foundation",
                table: "files",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "is_quarantined",
                schema: "foundation",
                table: "files");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_quarantined",
                schema: "foundation",
                table: "files",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE foundation.files SET is_quarantined = processing_state <> 'verified'");

            migrationBuilder.AlterColumn<bool>(
                name: "is_quarantined",
                schema: "foundation",
                table: "files",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "extension",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "mime_type",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "original_file_name",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "processing_state",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "retention_until",
                schema: "foundation",
                table: "files");

            migrationBuilder.DropColumn(
                name: "size_bytes",
                schema: "foundation",
                table: "files");

        }
    }
}
