using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class AddPaguaiRequestProofFile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            ALTER TABLE paguai.requests ADD COLUMN proof_file_id uuid NULL;

            -- A troca de comprovante usa verificação otimista: versão passa a
            -- crescer (expectedVersion -> expectedVersion + 1).
            ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_version;
            ALTER TABLE paguai.requests
                ADD CONSTRAINT ck_paguai_requests_version CHECK (version >= 1);

            -- Um comprovante só pode estar vinculado a um rascunho; o índice
            -- parcial único torna duplo vínculo impossível no storage.
            CREATE UNIQUE INDEX paguai.uq_paguai_requests_proof_file
                ON paguai.requests (proof_file_id)
                WHERE proof_file_id IS NOT NULL;

            -- Integridade referencial empresa+arquivo contra foundation.files.
            ALTER TABLE paguai.requests
                ADD CONSTRAINT fk_paguai_requests_proof_file
                FOREIGN KEY (company_id, proof_file_id)
                REFERENCES foundation.files (company_id, id)
                ON DELETE RESTRICT;

            -- PATCH do rascunho troca o comprovante na mesma transação da auditoria.
            GRANT UPDATE ON TABLE paguai.requests TO vinx_runtime;
            """
        );

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            REVOKE UPDATE ON TABLE paguai.requests FROM vinx_runtime;

            ALTER TABLE paguai.requests DROP CONSTRAINT fk_paguai_requests_proof_file;
            DROP INDEX paguai.uq_paguai_requests_proof_file;

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

            ALTER TABLE paguai.requests DROP COLUMN proof_file_id;

            ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_version;
            ALTER TABLE paguai.requests
                ADD CONSTRAINT ck_paguai_requests_version CHECK (version = 1);
            """
        );
}
