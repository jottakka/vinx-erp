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

            // Privilégio mínimo: o runtime só pode atualizar o ponteiro do
            // comprovante; valor, beneficiário, estado, requerente, fatura e
            // demais campos financeiros seguem intocáveis pelo SQL de runtime.
            // A troca versionada (CAS em `version`) chega em PR empilhado.
            migrationBuilder.Sql(
                "GRANT UPDATE (proof_file_id) ON paguai.requests TO vinx_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversão fail-closed: o migrator é dono da tabela, mas FORCE RLS
            // esconde toda linha sem contexto de empresa. A trava ACCESS
            // EXCLUSIVE é tomada ANTES de ler o estado e vale até o fim da
            // transação (revoke + drops), então um vínculo concorrente não pode
            // commitar entre a leitura da guarda e a remoção dos objetos; a
            // transação da migration reverte o NO FORCE em caso de recusa.
            migrationBuilder.Sql(
                """
                ALTER TABLE paguai.requests NO FORCE ROW LEVEL SECURITY;

                DO $block$
                BEGIN
                    LOCK TABLE paguai.requests IN ACCESS EXCLUSIVE MODE;
                    IF EXISTS (
                        SELECT 1 FROM paguai.requests
                        WHERE proof_file_id IS NOT NULL)
                    THEN
                        RAISE EXCEPTION 'cannot downgrade Paguai proof binding while bound files exist'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                ALTER TABLE paguai.requests FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.Sql(
                "REVOKE UPDATE (proof_file_id) ON paguai.requests FROM vinx_runtime;");

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
        }
    }
}
