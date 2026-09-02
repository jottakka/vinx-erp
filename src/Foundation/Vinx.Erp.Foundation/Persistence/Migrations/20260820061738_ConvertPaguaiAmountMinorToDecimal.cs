using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConvertPaguaiAmountMinorToDecimal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE paguai.requests
                    RENAME COLUMN amount_minor TO amount;

                ALTER TABLE paguai.requests
                    ALTER COLUMN amount TYPE numeric(18,2)
                    USING (amount / 100.0);

                -- O migrator é dono da tabela, mas FORCE RLS esconde toda linha
                -- sem app.company_id: o UPDATE rodaria cego (no-op silencioso).
                -- Visibilidade do dono restaurada no mesmo lote, com a trava
                -- ACCESS EXCLUSIVE valendo até o fim da transação; a transação
                -- da migration desfaz o NO FORCE em caso de recusa.
                ALTER TABLE foundation.idempotency_records NO FORCE ROW LEVEL SECURITY;

                DO $block$
                BEGIN
                    LOCK TABLE foundation.idempotency_records IN ACCESS EXCLUSIVE MODE;

                    UPDATE foundation.idempotency_records
                    SET result_json = jsonb_set(
                        result_json - 'AmountMinor',
                        '{Amount}',
                        to_jsonb(((result_json ->> 'AmountMinor')::numeric / 100)::text),
                        true)
                    WHERE command_name = 'paguai.draft.create'
                        AND result_json ? 'AmountMinor';
                END;
                $block$;

                ALTER TABLE foundation.idempotency_records FORCE ROW LEVEL SECURITY;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- Reversão fail-closed no padrão do 20260829060000: o migrator é
                -- dono das tabelas, mas FORCE RLS esconde toda linha sem
                -- contexto, então as guardas leriam zero linhas e o ALTER
                -- multiplicaria os valores ×100. A visibilidade é restaurada no
                -- mesmo lote (revertida em caso de recusa) e a trava ACCESS
                -- EXCLUSIVE é tomada ANTES das leituras e mantida até o fim da
                -- transação.
                ALTER TABLE paguai.requests NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE foundation.idempotency_records NO FORCE ROW LEVEL SECURITY;

                DO $$
                BEGIN
                    LOCK TABLE paguai.requests IN ACCESS EXCLUSIVE MODE;
                    LOCK TABLE foundation.idempotency_records IN ACCESS EXCLUSIVE MODE;

                    IF EXISTS (SELECT 1 FROM paguai.requests)
                        OR EXISTS (
                            SELECT 1
                            FROM foundation.idempotency_records
                            WHERE command_name = 'paguai.draft.create'
                        )
                    THEN
                        RAISE EXCEPTION 'ConvertPaguaiAmountMinorToDecimal Down requires empty paguai.requests and no paguai.draft.create idempotency receipts';
                    END IF;
                END
                $$;

                ALTER TABLE foundation.idempotency_records FORCE ROW LEVEL SECURITY;
                ALTER TABLE paguai.requests FORCE ROW LEVEL SECURITY;

                ALTER TABLE paguai.requests
                    ALTER COLUMN amount TYPE bigint
                    USING ((amount * 100)::bigint);

                ALTER TABLE paguai.requests
                    RENAME COLUMN amount TO amount_minor;
                """
            );
        }
    }
}
