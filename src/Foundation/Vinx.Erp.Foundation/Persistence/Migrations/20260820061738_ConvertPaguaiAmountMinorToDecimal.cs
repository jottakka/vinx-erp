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

                UPDATE foundation.idempotency_records
                SET result_json = jsonb_set(
                    result_json - 'AmountMinor',
                    '{Amount}',
                    to_jsonb(((result_json ->> 'AmountMinor')::numeric / 100)::text),
                    true)
                WHERE command_name = 'paguai.draft.create'
                    AND result_json ? 'AmountMinor';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
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
