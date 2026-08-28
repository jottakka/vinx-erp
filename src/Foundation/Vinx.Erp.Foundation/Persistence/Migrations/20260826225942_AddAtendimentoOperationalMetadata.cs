using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAtendimentoOperationalMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel",
                schema: "service_desk",
                table: "atendimento_cards",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "priority",
                schema: "service_desk",
                table: "atendimento_cards",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "media");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sla_due_at",
                schema: "service_desk",
                table: "atendimento_cards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_atendimento_cards_company_id_created_at_id_live",
                schema: "service_desk",
                table: "atendimento_cards",
                columns: new[] { "company_id", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.Sql(
                """
                ALTER TABLE service_desk.atendimento_cards
                    ADD CONSTRAINT ck_atendimento_cards_priority
                    CHECK (priority IN ('critica', 'alta', 'media', 'baixa')),
                    ADD CONSTRAINT ck_atendimento_cards_channel
                    CHECK (channel IN ('whatsapp', 'email', 'web', 'manual'));
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- FORCE RLS hides every company from the migrator without a transaction context.
                -- Restore the owner visibility only long enough for the fail-closed check, then
                -- reapply FORCE before any destructive DDL; a refusal rolls the transaction back.
                ALTER TABLE service_desk.atendimento_cards NO FORCE ROW LEVEL SECURITY;

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM service_desk.atendimento_cards
                        WHERE priority <> 'media'
                           OR channel <> 'manual'
                           OR sla_due_at IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot remove Atendimento operational metadata while non-default values exist.'
                            USING ERRCODE = '23514';
                    END IF;
                END $$;

                ALTER TABLE service_desk.atendimento_cards FORCE ROW LEVEL SECURITY;
                """
            );

            migrationBuilder.Sql(
                """
                ALTER TABLE service_desk.atendimento_cards
                    DROP CONSTRAINT ck_atendimento_cards_channel,
                    DROP CONSTRAINT ck_atendimento_cards_priority;
                """
            );

            migrationBuilder.DropIndex(
                name: "ix_atendimento_cards_company_id_created_at_id_live",
                schema: "service_desk",
                table: "atendimento_cards");

            migrationBuilder.DropColumn(
                name: "channel",
                schema: "service_desk",
                table: "atendimento_cards");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "service_desk",
                table: "atendimento_cards");

            migrationBuilder.DropColumn(
                name: "sla_due_at",
                schema: "service_desk",
                table: "atendimento_cards");
        }
    }
}
