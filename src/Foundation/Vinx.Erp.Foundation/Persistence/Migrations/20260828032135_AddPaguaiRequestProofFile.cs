using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaguaiRequestProofFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "proof_file_id",
                schema: "paguai",
                table: "requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_requests_company_id_proof_file_id",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "proof_file_id" });

            migrationBuilder.CreateIndex(
                name: "uq_paguai_requests_proof_file",
                schema: "paguai",
                table: "requests",
                column: "proof_file_id",
                unique: true,
                filter: "proof_file_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_paguai_requests_proof_file",
                schema: "paguai",
                table: "requests",
                columns: new[] { "company_id", "proof_file_id" },
                principalSchema: "foundation",
                principalTable: "files",
                principalColumns: new[] { "company_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // A troca de comprovante usa verificação otimista: a versão passa a
            // crescer (expectedVersion -> expectedVersion + 1).
            migrationBuilder.Sql(
                """
                ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_version;
                ALTER TABLE paguai.requests
                    ADD CONSTRAINT ck_paguai_requests_version CHECK (version >= 1);
                """);

            // Privilégio mínimo: o runtime só pode atualizar as colunas da troca
            // de comprovante; valor, beneficiário, estado, requerente, fatura e
            // demais campos financeiros seguem intocáveis pelo SQL de runtime.
            migrationBuilder.Sql(
                "GRANT UPDATE (proof_file_id, version) ON paguai.requests TO vinx_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversão fail-closed: recusa enquanto houver comprovante vinculado
            // ou versão avançada, antes de tocar em qualquer objeto.
            migrationBuilder.Sql(
                """
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM paguai.requests
                        WHERE proof_file_id IS NOT NULL OR version <> 1)
                    THEN
                        RAISE EXCEPTION 'cannot downgrade Paguai proof binding while bound files or bumped versions exist'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;
                """);

            migrationBuilder.Sql(
                "REVOKE UPDATE (proof_file_id, version) ON paguai.requests FROM vinx_runtime;");

            migrationBuilder.DropForeignKey(
                name: "fk_paguai_requests_proof_file",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "ix_requests_company_id_proof_file_id",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "uq_paguai_requests_proof_file",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "proof_file_id",
                schema: "paguai",
                table: "requests");

            // Restaura a invariante anterior: rascunho imutável (version = 1).
            migrationBuilder.Sql(
                """
                ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_version;
                ALTER TABLE paguai.requests
                    ADD CONSTRAINT ck_paguai_requests_version CHECK (version = 1);
                """);
        }
    }
}
