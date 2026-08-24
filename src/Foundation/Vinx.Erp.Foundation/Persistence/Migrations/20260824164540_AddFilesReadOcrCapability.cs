using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilesReadOcrCapability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // UUID estável (v5) registrado em KnownCapabilities.FilesReadOcrId;
            // conflito de nome é idempotente, colisão de id falha a migração.
            migrationBuilder.Sql(
                """
                INSERT INTO foundation.capabilities (id, name, created_at)
                VALUES
                    ('f9df3888-cadb-5344-bc68-3555b99dae86', 'foundation.files.read-ocr', clock_timestamp())
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
                            ('f9df3888-cadb-5344-bc68-3555b99dae86'::uuid, 'foundation.files.read-ocr'));
                EXCEPTION
                    WHEN foreign_key_violation THEN
                        RAISE EXCEPTION 'cannot downgrade foundation.files.read-ocr capability while it is referenced'
                            USING ERRCODE = '23503';
                END;
                $block$;
                """);

        }
    }
}
