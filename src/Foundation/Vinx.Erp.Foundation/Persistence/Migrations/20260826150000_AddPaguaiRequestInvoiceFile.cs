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
                -- The migrator owns this table, but FORCE RLS hides every row without a
                -- company context. Temporarily restore the owner's migration visibility so
                -- the fail-closed check sees references from every company. The migration
                -- transaction rolls this back on refusal and restores FORCE before a safe Down.
                ALTER TABLE paguai.requests NO FORCE ROW LEVEL SECURITY;

                DO $block$
                BEGIN
                    -- Trava ANTES da leitura da guarda, vale até o fim da
                    -- transação: nenhum vínculo concorrente commite entre a
                    -- checagem e os drops (padrão do 20260828032135).
                    LOCK TABLE paguai.requests IN ACCESS EXCLUSIVE MODE;

                    IF EXISTS (
                        SELECT 1 FROM paguai.requests
                        WHERE invoice_file_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'cannot downgrade paguai invoice file while drafts reference files'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                ALTER TABLE paguai.requests FORCE ROW LEVEL SECURITY;
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
