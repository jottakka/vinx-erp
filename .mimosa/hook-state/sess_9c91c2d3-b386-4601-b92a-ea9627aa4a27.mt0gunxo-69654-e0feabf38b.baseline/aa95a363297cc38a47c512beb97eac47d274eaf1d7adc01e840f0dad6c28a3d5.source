using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiWritesConfirmCapability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // UUID estável (v5) registrado em KnownCapabilities.AiWritesConfirmId;
            // conflito de nome é idempotente, colisão de id falha a migração.
            migrationBuilder.Sql(
                """
                INSERT INTO foundation.capabilities (id, name, created_at)
                VALUES
                    ('9e77f769-2093-55a5-a482-85ed3e83dc88', 'ai.writes.confirm', clock_timestamp())
                ON CONFLICT (name) DO NOTHING;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $block$
                BEGIN
                    DELETE FROM foundation.capabilities
                    WHERE (id, name) IN (
                            ('9e77f769-2093-55a5-a482-85ed3e83dc88'::uuid, 'ai.writes.confirm'));
                EXCEPTION
                    WHEN foreign_key_violation THEN
                        RAISE EXCEPTION 'cannot downgrade ai.writes.confirm capability while it is referenced'
                            USING ERRCODE = '23514';
                END;
                $block$;
                """);

        }
    }
}
