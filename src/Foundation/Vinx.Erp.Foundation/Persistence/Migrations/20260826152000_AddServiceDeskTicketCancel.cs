using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceDeskTicketCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                INSERT INTO foundation.capabilities (id, name, description, created_at)
                VALUES
                    ('ffe3ae25-dab3-5aaa-acde-b56f9797ce01', 'service_desk.ticket.cancel', 'Cancelar cartão de Atendimento', clock_timestamp())
                ON CONFLICT (name) DO NOTHING;
                """
            );

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                DO $block$
                BEGIN
                    DELETE FROM foundation.capabilities
                    WHERE (id, name) IN (
                            ('ffe3ae25-dab3-5aaa-acde-b56f9797ce01'::uuid, 'service_desk.ticket.cancel'));
                EXCEPTION
                    WHEN foreign_key_violation THEN
                        RAISE EXCEPTION 'cannot downgrade service_desk.ticket.cancel capability while it is referenced'
                            USING ERRCODE = '23503';
                END;
                $block$;
                """
            );
    }
}
