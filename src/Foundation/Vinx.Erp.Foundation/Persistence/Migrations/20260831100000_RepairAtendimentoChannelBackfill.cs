using Microsoft.EntityFrameworkCore.Migrations;
using Vinx.Erp.Foundation.Persistence;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <summary>
/// Reparo do backfill de canais do Atendimento: o seed do 20260827000000 rodou
/// cego — o RLS FORCE de foundation.companies esconde toda linha do
/// vinx_migrator (NOBYPASSRLS, sem app.company_id), então nenhuma empresa
/// pré-existente recebeu os canais padrão. Repete a semeadura com a
/// visibilidade do dono restaurada no mesmo lote; sem mudança de modelo EF.
/// </summary>
public partial class RepairAtendimentoChannelBackfill : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Padrão das reversões fail-closed (20260828032135/20260829060000),
        // aplicado a um backfill: NO FORCE devolve a visibilidade do dono,
        // a trava ACCESS EXCLUSIVE vale até o fim da transação (um INSERT
        // concorrente não pode commitar entre a leitura e a semeadura) e o
        // FORCE é restaurado no mesmo lote — a transação da migration desfaz
        // o NO FORCE em caso de recusa.
        migrationBuilder.Sql(
            """
            ALTER TABLE foundation.companies NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_channels NO FORCE ROW LEVEL SECURITY;

            DO $block$
            BEGIN
                LOCK TABLE foundation.companies IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE service_desk.atendimento_channels IN ACCESS EXCLUSIVE MODE;

                -- Mesmo SELECT do backfill original do 20260827000000; a
                -- guarda idempotente (ON CONFLICT) tolera reexecução.
                INSERT INTO service_desk.atendimento_channels (id, company_id, key)
                SELECT gen_random_uuid(), company.id, candidate.key
                FROM foundation.companies AS company
                CROSS JOIN (VALUES ('whatsapp'), ('email'), ('web'), ('manual')) AS candidate(key)
                WHERE company.deleted_at IS NULL
                ON CONFLICT (company_id, key) DO NOTHING;
            END;
            $block$;

            ALTER TABLE service_desk.atendimento_channels FORCE ROW LEVEL SECURITY;
            ALTER TABLE foundation.companies FORCE ROW LEVEL SECURITY;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reparo de dados não é reversível: remover os canais aqui apagaria
        // canais legítimos, inclusive os criados pelo trigger para empresas
        // novas. Down vazio de propósito.
    }
}
