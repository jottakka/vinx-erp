using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameExternalIdentityProviderToIssuer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_external_identities_provider_subject",
                schema: "foundation",
                table: "external_identities");

            migrationBuilder.RenameColumn(
                name: "provider",
                schema: "foundation",
                table: "external_identities",
                newName: "issuer");

            migrationBuilder.AlterColumn<string>(
                name: "issuer",
                schema: "foundation",
                table: "external_identities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_issuer_subject",
                schema: "foundation",
                table: "external_identities",
                columns: new[] { "issuer", "subject" },
                unique: true);

            migrationBuilder.Sql("GRANT SELECT ON TABLE foundation.external_identities TO vinx_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_external_identities_issuer_subject",
                schema: "foundation",
                table: "external_identities");

            migrationBuilder.AlterColumn<string>(
                name: "issuer",
                schema: "foundation",
                table: "external_identities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.RenameColumn(
                name: "issuer",
                schema: "foundation",
                table: "external_identities",
                newName: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_provider_subject",
                schema: "foundation",
                table: "external_identities",
                columns: new[] { "provider", "subject" },
                unique: true);

            migrationBuilder.Sql("REVOKE ALL ON TABLE foundation.external_identities FROM vinx_runtime;");
        }
    }
}
