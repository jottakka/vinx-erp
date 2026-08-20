using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaguaiFinancialDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM foundation.idempotency_records
                WHERE command_name = 'paguai.draft.create';

                DELETE FROM paguai.requests;
                """
            );

            migrationBuilder.AddColumn<long>(
                name: "amount_minor",
                schema: "paguai",
                table: "requests",
                type: "bigint",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "beneficiary_display_name",
                schema: "paguai",
                table: "requests",
                type: "text",
                maxLength: 200,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "paguai",
                table: "requests",
                type: "text",
                maxLength: 3,
                nullable: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "due_date",
                schema: "paguai",
                table: "requests",
                type: "date",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "proof_requirement",
                schema: "paguai",
                table: "requests",
                type: "text",
                maxLength: 10,
                nullable: false);

            migrationBuilder.Sql(
                """
                ALTER TABLE paguai.requests
                    ADD CONSTRAINT ck_paguai_requests_currency_brl CHECK (currency = 'BRL');
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE paguai.requests DROP CONSTRAINT ck_paguai_requests_currency_brl;"
            );

            migrationBuilder.DropColumn(
                name: "amount_minor",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "beneficiary_display_name",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "due_date",
                schema: "paguai",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "proof_requirement",
                schema: "paguai",
                table: "requests");
        }
    }
}
