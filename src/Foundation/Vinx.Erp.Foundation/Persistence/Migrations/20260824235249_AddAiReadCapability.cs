using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiReadCapability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO foundation.capabilities (id, name, created_at)
                VALUES
                    ('46db8e9e-5bd5-59ea-a371-0312ba8fafb8', 'ai.read', clock_timestamp())
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
                            ('46db8e9e-5bd5-59ea-a371-0312ba8fafb8'::uuid, 'ai.read'));
                EXCEPTION
                    WHEN foreign_key_violation THEN
                        RAISE EXCEPTION 'cannot downgrade ai.read capability while it is referenced'
                            USING ERRCODE = '23503';
                END;
                $block$;
                """);
        }
    }
}
