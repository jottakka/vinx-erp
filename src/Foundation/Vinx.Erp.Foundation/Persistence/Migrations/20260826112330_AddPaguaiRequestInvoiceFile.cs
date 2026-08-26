using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaguaiRequestInvoiceFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "invoice_file_id",
                schema: "paguai",
                table: "requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_requests_company_id_invoice_file_id",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "invoice_file_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_requests_files_company_id_invoice_file_id",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "invoice_file_id" },
                principalSchema: "foundation",
                principalTable: "files",
                principalColumns: new[] { "company_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM paguai.requests
                        WHERE invoice_file_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'cannot downgrade paguai invoice file while drafts reference files'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;
                """
            );
            migrationBuilder.DropForeignKey(
                name: "fk_requests_files_company_id_invoice_file_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "ix_requests_company_id_invoice_file_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "invoice_file_id",
                schema: "paguai",
                table: "requests");
        }
    }
}
