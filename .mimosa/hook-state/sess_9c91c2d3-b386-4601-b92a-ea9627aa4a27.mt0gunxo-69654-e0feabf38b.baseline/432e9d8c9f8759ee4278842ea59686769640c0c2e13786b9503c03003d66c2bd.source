using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoundationCapabilityCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO foundation.capabilities (id, name, created_at)
                VALUES
                    ('00341b57-025e-5a71-a205-f0a6854a991f', 'foundation.roles.assign', clock_timestamp()),
                    ('e09997ad-8dda-58f6-8e9f-568bf84c9ab3', 'foundation.files.upload', clock_timestamp()),
                    ('4687cfe7-2318-58ca-8ac9-fff0dad042b0', 'foundation.files.confirm', clock_timestamp()),
                    ('46285f1b-bda6-5fba-b0e9-74515120b076', 'foundation.memberships.revoke', clock_timestamp())
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
                            ('00341b57-025e-5a71-a205-f0a6854a991f'::uuid, 'foundation.roles.assign'),
                            ('e09997ad-8dda-58f6-8e9f-568bf84c9ab3'::uuid, 'foundation.files.upload'),
                            ('4687cfe7-2318-58ca-8ac9-fff0dad042b0'::uuid, 'foundation.files.confirm'),
                            ('46285f1b-bda6-5fba-b0e9-74515120b076'::uuid, 'foundation.memberships.revoke'));
                EXCEPTION
                    WHEN foreign_key_violation THEN
                        RAISE EXCEPTION 'cannot downgrade Foundation capability catalog while capabilities are referenced'
                            USING ERRCODE = '23503';
                END;
                $block$;
                """);

        }
    }
}
