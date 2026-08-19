using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileScanMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE foundation.files
                    ADD COLUMN IF NOT EXISTS scan_engine character varying(100),
                    ADD COLUMN IF NOT EXISTS scan_engine_version character varying(50),
                    ADD COLUMN IF NOT EXISTS scan_signature_version character varying(50),
                    ADD COLUMN IF NOT EXISTS scanned_at_utc timestamp with time zone;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE foundation.files
                    DROP COLUMN IF EXISTS scan_engine;
                ALTER TABLE foundation.files
                    DROP COLUMN IF EXISTS scan_engine_version;
                ALTER TABLE foundation.files
                    DROP COLUMN IF EXISTS scan_signature_version;
                ALTER TABLE foundation.files
                    DROP COLUMN IF EXISTS scanned_at_utc;
                """);
        }
    }
}
