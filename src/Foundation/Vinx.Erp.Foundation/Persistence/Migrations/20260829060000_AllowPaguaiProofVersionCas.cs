using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <summary>
/// Fatia empilhada do #345: habilita a troca versionada (CAS) do comprovante.
/// O runtime passa a poder atualizar apenas <c>version</c> além do ponteiro
/// <c>proof_file_id</c> concedido pela migration anterior; nenhum outro campo
/// financeiro vira atualizável. Sem mudança de modelo EF — apenas SQL cru.
/// </summary>
[DbContext(typeof(FoundationDbContext))]
[Migration("20260829060000_AllowPaguaiProofVersionCas")]
public partial class AllowPaguaiProofVersionCas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A troca de comprovante usa verificação otimista: a versão passa a
        // crescer (expectedVersion -> expectedVersion + 1) a partir de 1.
        migrationBuilder.Sql(
            """
            ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_version;
            ALTER TABLE paguai.requests
                ADD CONSTRAINT ck_paguai_requests_version CHECK (version >= 1);
            """);

        // Privilégio mínimo: o CAS exige atualizar apenas a coluna da versão;
        // valor, beneficiário, estado, requerente, fatura e demais campos
        // financeiros seguem intocáveis pelo SQL de runtime.
        migrationBuilder.Sql(
            "GRANT UPDATE (version) ON paguai.requests TO vinx_runtime;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reversão fail-closed no padrão da migration de prova: o migrator é
        // dono da tabela, mas FORCE RLS esconde toda linha sem contexto, então
        // a visibilidade é restaurada no mesmo lote (revertida em caso de
        // recusa) e a trava ACCESS EXCLUSIVE é tomada ANTES da leitura e mantida
        // até o fim da transação — um bump concorrente não pode commitar entre
        // a guarda e a restauração da invariante.
        migrationBuilder.Sql(
            """
            ALTER TABLE paguai.requests NO FORCE ROW LEVEL SECURITY;

            DO $block$
            BEGIN
                LOCK TABLE paguai.requests IN ACCESS EXCLUSIVE MODE;
                IF EXISTS (
                    SELECT 1 FROM paguai.requests
                    WHERE version <> 1)
                THEN
                    RAISE EXCEPTION 'cannot downgrade Paguai proof version CAS while bumped versions exist'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            ALTER TABLE paguai.requests FORCE ROW LEVEL SECURITY;
            """);

        migrationBuilder.Sql(
            "REVOKE UPDATE (version) ON paguai.requests FROM vinx_runtime;");

        // Restaura a invariante anterior: rascunho imutável (version = 1).
        migrationBuilder.Sql(
            """
            ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_version;
            ALTER TABLE paguai.requests
                ADD CONSTRAINT ck_paguai_requests_version CHECK (version = 1);
            """);
    }
}
